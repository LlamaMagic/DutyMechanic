# Duty progression audit

Started 2026-10-23. Run duties in progression order on Arelia Pendragon, using
level-synced Duty Support where available. Compare both awgil/ffxiv_bossmod and
FFXIV-CombatReborn/BossmodReborn. A clear is not evidence that every mechanic ran.
Record casts, positioning at resolution, failures, and any unexercised branches.

## Current run

- Arelia: RebornMCP `http://127.0.0.1:8451/mcp`, level 100 outside duties.
  Sastasha/Tam-Tara tested on Red Mage; Copperbell uses her existing Paladin set.
- Live host: `C:\DataHome\Backupstuff\Reborn - Magitech - Copy`.
- Source under maintenance: `C:\DataHome\Backupstuff\Reborn - Magitech\Plugins\DutyMechanic`.
- Other connected characters were identified read-only and are not audit runners.
- Sastasha (InstanceContent 4, territory 1036), Duty Support, level 18 sync: cleared
  with no player deaths. Two Charged Whiskers, two Hydroballs, both Madison add
  encounters, and Baleen Guard targeting were observed.
- Tam-Tara (InstanceContent 2, territory 1037), Duty Support, level 19 sync:
  cleared with no player deaths. Two Condemnations, Mind Blast, and imp targeting
  in three shield phases were captured. Third counter died before using its cone.
- Copperbell (InstanceContent 3, territory 1038), Duty Support, level 20 sync:
  cleared on Paladin with no player deaths. Confirmed active class
  `DutyAuditCopperbellFinal.CopperbellMines` and correct Copperbell profile.
  Original Red Mage gearset index 20 is retained as `DutyAuditOriginalGearsetIndex`
  for restoration; Paladin's existing set is index 2.
- Restored Red Mage gearset 20 after Copperbell. Bowl of Embers (territory 1045,
  duty 56), Duty Support, is next/current.
- The authenticated Panda duty profile owns traversal, switches, doors, and combat.
  DutyMechanic and SideStep own encounter positioning. No protected profile XML
  is reproduced here.
- Temporary `[DutyAudit]` telemetry records selected enemy cast starts,
  positions within 300 ms of completion, and cast disappearance. Disappearance
  alone does not distinguish cancellation from successful resolution.

## Comparison: first three duties

The existing handlers enable SideStep on entry. Empty action lists do not mean
there is no avoidance: SideStep reads cast types and omen geometry. Compare that
generated geometry and live behavior before adding duplicate avoids.

| Duty / mechanic | Reference behavior | Current implementation / verification |
| --- | --- | --- |
| Sastasha: Charged Whisker (351) | Chopper-centered circle, radius 6.15 | SideStep detected both observed casts; first near-end distance 12.648; second actively escaped to 8.014 by disappearance; no HP loss/paralysis observed; see evidence notes |
| Sastasha: both Madison encounters | Attackable reavers and dogs | Added encounter-scoped priority; both encounters observed attacking adds while Madison was alive, then returning to Madison |
| Sastasha: Hydroball (556) | Denn-facing 90-degree cone, range 8 | SideStep omen 4; both observed casts had near-end samples, two generated avoids, and no HP drop on disappearance |
| Sastasha: Baleen Guards / ripples | Adds take priority; Reborn also displays active ripples | Recorded direct spell casts against three Baleen Guards. Ripple hinting is informational in reference and is not mirrored as an interaction policy |
| Sastasha: coral switch | Infer correct coral from targetable Bloody Memo ID | Profile owns interactions; observed red then blue (wrong-switch poison/adds), then green and successful inconspicuous-switch interaction. Traversal works by sequential fallback; memo optimization is not implemented |
| Tam-Tara: Condemnation (912) | 90-degree cone, range 7 | First two encounters: captured outside avoidance 82.5 / 275.1 ms before completion, HP stayed 424; third counter died before casting |
| Tam-Tara: Mind Blast (987) | Galvanth-centered circle, radius 9.95 | At 284 ms remaining, distance 16.949 and outside avoidance; HP stayed 423 through disappearance |
| Tam-Tara: imps and other adds | Prioritize imps, which shield Galvanth | Profile selected imps in all three shield phases; direct spell casts against both final-phase imps confirmed. No handler targeting change needed |
| Copperbell: Grand Slam (28545), Fluid Spread (28461), Grand Slam (28764) | Single-target tankbusters | All detected; Rampart accepted for Kottos and Ire; Rampart buff confirmed for Kottos; Reprisal debuff confirmed on Gyges near resolution |
| Copperbell: Lumbering Leap (28544) | Helper circles, radius 12 | Six near-end samples across two three-helper patterns were outside all avoids and beyond radius 12 |
| Copperbell: Colossal Slam (28546) | 60-degree cone, range 30 | Near-end captures outside avoidance, no mechanic failure observed |
| Copperbell: Catapult (28547) | Unavoidable single-target damage | Captured with no avoid generated, matching reference classification |
| Copperbell: Syrup (28462) | Ground-targeted circle, radius 4 | Three safe near-end samples; use captured ground origin for distance checks |
| Copperbell: Divide / Burst (28465) | Multiple drip-centered circles, radius 8 | Two six-helper patterns captured safely; later minimum sampled distance 8.595 |
| Copperbell: Gigantic Swing (28762) | Donut, inner radius 4 / outer radius 40 | New 3.5/40.5 donut passed native containment and three live casts; near-end distances 3.491, 3.406, 3.499; SideStep deferred |
| Copperbell: Gigantic Smash (28760) | Ground-targeted circle, radius 10 | Captured at distance 11.644 from its ground origin, outside avoidance |
| Copperbell: Gigantic Blast (28761) | Helper circles, radius 8 | Five-helper pattern captured outside all avoids; minimum sampled distance 8.684 |
| Copperbell: Colossal Slam (28763) | 60-degree cone, range 40 | Captured outside avoidance; no mechanic failure observed |

SideStep currently expands ordinary cone angles by 1.55 and circle radii by
0.5. These are conservative differences from BossMod, not exact geometric parity.
Do not change shared SideStep behavior as part of an individual-duty repair.

## Sources

- [BossMod Sastasha](https://github.com/awgil/ffxiv_bossmod/tree/master/BossMod.Modules/RealmReborn/Dungeon/D01Sastasha)
- [BossMod Tam-Tara](https://github.com/awgil/ffxiv_bossmod/tree/master/BossMod.Modules/RealmReborn/Dungeon/D02TamTara)
- [BossMod Copperbell](https://github.com/awgil/ffxiv_bossmod/tree/master/BossMod.Modules/RealmReborn/Dungeon/D03Copperbell)
- [BossmodReborn snapshot ae64e3309905dafad0952dbe72680add167591f9](https://github.com/FFXIV-CombatReborn/BossmodReborn/tree/ae64e3309905dafad0952dbe72680add167591f9/BossMod/Modules/RealmReborn/Dungeon)

## Validation and boundaries

- Full DutyMechanic Debug/x64 build passed with 0 errors and 15 existing warnings.
  The sandbox cannot probe the per-user Windows SDK folder; pass explicit
  `TargetPlatformSdkPath` and `TargetPlatformDisplayName` MSBuild properties.
- After Domestic restarted the stalled host, Arelia reconnected on port 8451 in
  Limsa Lominsa, with OrderBot stopped. A fresh level-synced Sastasha run started.
- The host's folder loader rejects a source folder outside its own Plugins root.
  For this session only, the exact Sastasha and Copperbell source handlers were
  runtime-compiled with unique namespaces and installed in their existing two
  dungeon-manager entries. The original entries are retained in
  `DutyAuditOriginalHandlers` for cleanup. No shared handler or installed source
  file was replaced. A host restart also removes these temporary mappings.
- Updated Halatali, Haukke, and Brayflox classes were subsequently compiled and
  staged the same way for their future entries; their original mappings were
  added to the same cleanup dictionary. They remain untested live.
- Temporary telemetry was installed after TreeRoot.Start, because starting the
  bot clears hooks. It now records cast discovery, a sample within 300 ms of cast
  completion, disappearance, target changes, and player deaths in territories
  1036/1037/1038. Early Sastasha trash casts have produced all three cast records.
- Existing Royal Menagerie and plugin-entry-point edits predate this audit.
- Yuweyawata and all Necrohazard-owned/shared behavior are frozen by AGENTS.md.
  No audit change has touched them.
- No later duty is live-verified yet. Next duties: Tam-Tara, Copperbell, Bowl of
  Embers, current Halatali, Toto-Rak, Haukke Manor, Brayflox, then onward by level.

## Comparison: current Halatali (territory 1245)

Both upstream BossMod and BossmodReborn agree on Burning Bolt (40591, helper),
Levinfang (40594), and Straight Punch (40602) as single-target tankbusters.
Added these IDs and the existing tankbuster handler call to `Halatali7_1`.
Live validation remains pending, including a tank role at level sync.

Corrected comments describing Fireflow as a stack: 40588/40589 are successive
45-degree cone waves, while Hydroelectric Shock (40593 visual / 41113 helper)
requires dry ground. Added 41113 to the existing follow-party action list: its
actual hit resolves one second after the boss's visual, so following must remain
active through the helper. Still verify the handoff between successive waves,
and that following the party reaches a dry pad before the helper resolves.

Other reference mechanics awaiting live checks: Fire II (40592, radius 5),
Electrify (40595, radius 6), Plain Pound/Tremblor/Earthquake (40599/40600/40601,
successive 0-10 / 10-20 / 20-30 rings), Firewater (41123, radius 3), and Burning
Ward's adds / boss invulnerability. SideStep and the authenticated traversal
profile must be checked together; a source comparison alone does not verify them.

Tangata now owns the concentric sequence locally. The currently casting stage
with the lowest action ID is active (Plain Pound 40599, Tremblor 40600,
Earthquake 40601). Geometry uses a 10.5-yalm circle, then 9.5-20.5 and 19.5-30.5
rings, allowing half a yalm of clearance against the reference 10 / 10-20 / 20-30
damage regions. All three avoid collections claim their casts from discovery,
while later waves remain inactive. This prevents generic future rings from
simultaneously forbidding the current safe area. A native RebornBuddy probe
confirmed an inactive `AvoidPolygonInfo` still exposes its collection, which is
what SideStep checks before choosing its generic decoder. Shared helpers and
SideStep were not modified. Live timing and navigation validation remain pending.

Both new ring polygons passed a native containment check at 360 angles for six
radii each, totaling 4,320 samples. This checks geometry, not stage timing. The
revised Halatali class is staged for its future live run.

Sources:

- [BossMod current Halatali](https://github.com/awgil/ffxiv_bossmod/tree/master/BossMod.Modules/RealmReborn/Dungeon/D04Halatali)
- [BossmodReborn current Halatali](https://github.com/FFXIV-CombatReborn/BossmodReborn/tree/main/BossMod/Modules/RealmReborn/Dungeon/D04Halatali)

## Copperbell donut geometry check

The installed SideStep `Avoid/Torus.cs` uses `range / 2.5` for the inner edge,
which does not match Gigantic Swing's four-yalm center. Further inspection of
live action metadata found omen 107 / cast type 10 for action 28762. SideStep
prioritizes omen handlers over cast types, and its omen-107 handler is a cone,
so neither generic path is an accurate model for this action. Copperbell now registers
the existing `AvoidanceHelpers.AddAvoidDonut` only for action 28762 in territory
1038, using radii 3.5/40.5 to allow half a yalm of clearance. Shared SideStep and
DutyMechanic avoidance helpers were not modified. SideStep checks existing avoid
collections before selecting its decoder; this cast-specific collection is
intended to make it defer. Verify that deferral in the live cast capture too.

In the live host, constructed the same polygon through the existing helper and
queried native RebornBuddy `AvoidPolygon.IsPointInAvoid` without registering it
with the running avoidance manager. Sampled 360 angles at each radius 0, 3, 4,
5, 10, 20, 39.9, and 41. All 2,880 samples matched the expected safe/damaging
regions. This checks polygon containment, not navigation or cast timing. The
updated Copperbell class is staged in memory for its next entry.

## Sastasha live evidence (ongoing)

Host session log: `69852 2026-10-23 19.57.txt`. Log timestamps below are the
host log's UTC clock; the session began during October 23 local time.

- `00:07:49.178` Charged Whisker discovered; `00:07:51.600` sample with 273.9 ms
  remaining showed Arelia 12.648 yalms from Chopper (reference radius 6.15), with
  one generated avoid and HP 409. HP stayed 409 through cast disappearance.
- `00:08:00.634` second Charged Whisker discovered with avoidance already
  escaping; `00:08:01.899` avoidance finished running. Position at disappearance
  was 8.014 yalms from Chopper, with HP 409 and no paralysis aura observed.
  The last 300-ms TreeStart sample was missed for this cast, so do not claim an
  exact impact-time position for it. The traversal proceeded after Chopper died.
- `00:09:06.758` a Shallowtail Reaver was selected; a live query confirmed the
  runtime-compiled Sastasha handler was active and Madison plus reavers were
  simultaneously alive and in combat. `00:09:34.695` targeting returned to
  Madison after the adds died.
- `00:14:56.036` through `00:15:08.534` recorded direct offensive casts against
  three Scurvy Dogs during second Madison, followed by the handler's explicit
  priority-target release and return to Madison at `00:15:09.103`.
- Hydroball near-end samples at `00:18:50.555` and `00:19:36.600` were 272 ms
  before completion, at distances 5.243 and 5.608. SideStep registered two avoids
  for each. HP stayed 409 through each disappearance; no silence aura was seen.
  Re-evaluating those scalar positions/headings with RebornBuddy's own
  `Clio.Common.MathEx.CalculateNeededFacing` gave angles 177.367 and 157.804
  degrees from Denn's forward direction, safely outside the reference's
  45-degree half-angle even though both positions were within its eight-yalm range.
- `00:19:28.613` through `00:19:39.127` recorded direct offensive casts against
  three Baleen Guards while Denn remained alive. The party defeated Denn and
  the profile left the instance at `00:19:53.593`. Arelia returned to territory
  138 with the bot stopped. No player-death event was recorded during this run.
- For subsequent runs, moved the same read-only collector into a temporary
  in-memory plugin observer. Plugin pulses continue while combat coroutines own
  TreeStart, improving cast-completion sampling. Its container is retained as
  `DutyAuditObserverContainer` for removal after testing; no plugin files were
  installed and no movement/targeting logic was added to the observer.

## Upcoming Haukke Manor and Brayflox checks

Both reference implementations confirm Haukke's Void Thunder III (28645) and
Brayflox's Salivous Snap (28659) as tankbusters. Added their per-duty allowlist
entries and existing mitigation calls. Neither change is live-verified yet.

| Duty | Reference mechanics to exercise | Existing behavior / unresolved checks |
| --- | --- | --- |
| Haukke: Claviger | Sweet Steel (489, untelegraphed cleave); Void Fire II (855, radius 5); Dark Mist (705, radius 9.4) | Check facing relative to the tank and SideStep cast avoids |
| Haukke: Steward / Jester | Soul Drain (860, radius 9); Ice Spikes (859, interruptible buff); Blizzard (967); Jester add priority | Check profile targets and routine interrupts; no dedicated interrupt policy in handler |
| Haukke: Amandine | Dark Mist (28646, radius 9); Beguiling Mist (28649, forced movement); Void Thunder III (28645); Petrifying Eye (28648); Handmaidens / Stoneskin (28641) | Existing gaze workaround starts a circle avoid with 500 ms left; verify actual look-away at impact. Check add priority and interrupt/mitigation fallback |
| Brayflox: Pelican | Numbing Breath (506, 120-degree cone/range 9.2); Violet/Sable Back adds | SideStep and profile targeting, live checks pending |
| Brayflox: Inferno Drake | Burning Cyclone (984, 0.5-second cone); Tempest Biast / Levinshower (985, 0.5-second cone) | Very short casts need live reaction/facing evidence |
| Brayflox: Hellbender | Peculiar Light (982, radius 8); Stagnant Spray (448); Bog Bubble (980); Queer Bubble target; Aiatar Touchdown (564) and Dragon Breath (29618) | Handler currently adds a two-yalm avoid around the bubble. Verify the profile actually kills it and avoids delaying trapped-player recovery |
| Brayflox: Aiatar | Salivous Snap (28659); Toxic Vomit helpers (28657, radius 2); Burst (28658, radius 10, staggered); Dragon Breath (28660, 30x8 rectangle) | SideStep timing for staggered helper bursts needs comparison; mitigation added, live pending |

Sources:

- [BossMod Haukke](https://github.com/awgil/ffxiv_bossmod/tree/master/BossMod.Modules/RealmReborn/Dungeon/D06Haukke)
- [BossmodReborn Haukke](https://github.com/FFXIV-CombatReborn/BossmodReborn/tree/main/BossMod/Modules/RealmReborn/Dungeon/D06Haukke)
- [BossMod Brayflox](https://github.com/awgil/ffxiv_bossmod/tree/master/BossMod.Modules/RealmReborn/Dungeon/D07Brayflox)
- [BossmodReborn Brayflox](https://github.com/FFXIV-CombatReborn/BossmodReborn/tree/main/BossMod/Modules/RealmReborn/Dungeon/D07Brayflox)

## Tam-Tara live evidence (ongoing)

- Entered territory 1037 at level 19 sync with Duty Support. The observer survived
  the bot stop/start transition and resumed without observer errors.
- `00:25:54.594` first Void Soulcounter's Condemnation discovered. SideStep
  issued two escape movements. At `00:25:56.712`, with 82.5 ms remaining, Arelia
  was 6.422 yalms from the caster and outside all current avoids (`inAvoid=False`).
  HP stayed 424 through cast disappearance at `00:25:57.151`. The counter was
  subsequently defeated and the profile advanced.
- Native facing calculation places the first sample 76.367 degrees from the
  caster's forward direction, outside the reference's 45-degree half-angle.
- `00:29:13.000` second counter's Condemnation discovered. At `00:29:14.925`,
  with 275.1 ms remaining, Arelia was 4.047 yalms from the caster and outside all
  current avoids. HP stayed 424 through disappearance at `00:29:15.398`; the
  counter was defeated and the profile continued.
- Third counter was defeated at `00:35:34.767` without casting Condemnation;
  its specific arena branch remains unexercised for this action.
- `00:36:49.968` Mind Blast discovered. At `00:36:54.384`, with 284 ms remaining,
  Arelia was 16.949 yalms from Galvanth (reference radius 9.95), outside the
  generated avoid. HP stayed 423 through disappearance at `00:36:54.891`.
- Imp targeting was recorded at `00:36:12.334`, `00:36:32.022`,
  `00:37:04.769`, and `00:37:13.960`. Offensive action logs confirm direct casts
  against both final-phase imps before returning to Galvanth at `00:37:22.673`.
  Imps expose NameID/NpcId 456 in this client (BossMod's actor OID is 0x7D).
- Galvanth was defeated at `00:37:44.945`. The profile returned Arelia to
  territory 138 and stopped. No player-death event was captured in this run.

## Copperbell live evidence (ongoing)

- Kottos: first Grand Slam was detected at `00:48:49.944`; the handler requested
  Rampart once at `00:48:49.946`. Status 1191 appeared at `00:48:50.680` and
  remained through the cast, expiring at `00:49:10.651`. Native
  `DataManager.GetAuraNameById(1191)` confirms this is Rampart. The next Grand
  Slam selected Reprisal once at `00:50:11.012`; its target debuff was not captured
  in that earlier observer version.
- Kottos: all six Lumbering Leap samples across two three-helper patterns were
  outside all current avoids 281-299 ms before completion. Distances from their
  twelve-yalm origins were 13.652, 13.998, 13.046, 18.785, 16.735, and 13.067.
  Colossal Slam and Catapult were also captured; no movement was generated for
  Catapult, matching its unavoidable single-target classification.
- Ichorous Ire: Fluid Spread selected Rampart at `00:53:34.943`. Syrup samples
  were outside avoidance; compare against its ground `castLocation`/`omenCenter`,
  not the displayed distance from the boss. Both observed Burst patterns had
  six helper casts. The later pattern's minimum sampled distance was 8.595
  against the reference radius eight, with `inAvoid=False` for every helper.
  The observer was subsequently enriched with player/caster aura names at cast
  completion to verify future mitigation effects directly.
- Gyges: SideStep's first detection for 28762 reported `avoidance manager: True`
  and did not select its generic decoder. Three Gigantic Swing near-end samples
  had distances 3.491, 3.406, and 3.499, with one avoid and `inAvoid=False`.
  Positions remained inside the reference's four-yalm center through cast
  disappearance, with stable/increasing HP. The casts used three different boss
  locations; the corrected origin followed the boss rather than stale castLocation.
- Gyges's Grand Slam at `00:58:08.368` selected Reprisal. At `00:58:12.790`,
  with 273.8 ms remaining, the caster had `1193:Reprisal`; Arelia also had
  `1191:Rampart` from the routine. A later Grand Slam was detected without an
  accepted defensive action; record cooldown availability rather than claiming
  every tankbuster can always be mitigated at this sync level.
- Gyges's Gigantic Smash sample was 11.644 yalms from its ten-yalm ground AOE.
  The subsequent five Gigantic Blast helpers had minimum distance 8.684 against
  radius eight. All those near-end samples were outside current avoidance.
- Copperbell completed and Arelia returned to territory 138 with no recorded
  player deaths. Red Mage gearset 20 was restored for the next duty.

## Bowl of Embers

Upstream BossMod models Incinerate as a sixteen-yalm frontal cleave. The existing
handler used radius fifteen; increased it to 16.5 for half a yalm of clearance,
retaining the existing conservative 160-degree arc and tank/target/party gates.
The Duty Support run completed on Red Mage with no recorded player deaths.
Eruption and Radiant Plume near-end samples were outside the registered avoids.
The party killed Infernal Nail within its deadline and survived Hellfire. Arelia
returned to territory 138 with the bot stopped. The exact outer edge of the
Incinerate cone was not isolated by the live probe and remains unverified.

The temporary runtime-compiled class forwarded its internal `IsTank` extension
to the installed method. This reflection adapter and the audit observer were
test fixtures and are excluded from the release source package.

- [BossMod normal Ifrit](https://github.com/awgil/ffxiv_bossmod/blob/master/BossMod.Modules/RealmReborn/Trial/T01IfritN/T01IfritN.cs)
