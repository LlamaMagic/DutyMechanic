# Ktisis Hyperboreia mechanics

This handler replaces NPC-following mechanics with encounter signals, native
avoidance and targeted positioning. It covers territory 974 and was verified
with Arelia, Magitek and the existing Duty Support profile on October 27, 2026.
The profile continues to own routing, completion and recovery.

Build **7.5.4282015.13** completed a full Viper run with zero recorded
vulnerability gains and zero deaths. The corrected self-targeted stack passed
replay checks, but subsequent live markers selected NPCs; that branch still
needs a live self-target capture. Paladin interruption was verified separately.
The final source cleanup changes comments, formatting and braces only; it does
not represent another deployed build or another gameplay run.

## Ownership and movement

DutyMechanic saves SideStep's enabled state and suspends it while a Ktisis boss
is present. Native avoidance owns hazard escape. One encounter movement owner
handles stacks, meteor shelters and the Frostbite safe hold, yielding immediately
to native escape. Cleanup releases only its own leases and restores SideStep on
stop, death, loading, room changes and exit. SideStep remains responsible for trash.

Positioning yields to the combat routine on arrival while retaining its Movement
lease. This allows healing, mitigation and rotation without leaving the mechanic.
A separate GapCloser lease prevents Magitek from using Slither across active
hazards after native escape has reached safety and released Movement. Both leases
expire or clear with the encounter; no Magitek settings or source are changed.

The three source files separate encounter decisions, hazard snapshots and native
signals. The plugin pulse provides lifecycle cleanup when TreeStart is bypassed
and captures impacts while another coroutine retains that hook. No shared
encounter geometry or protected Necrohazard behavior is changed.

## Mechanic coverage

Dimensions below include the default 0.5-yalm hazard margin. Positions are XZ;
Lyssa and Hermes use a 19-yalm circular boundary, while Ladon uses a 38-by-38
square so valid corner positions remain available.

| Encounter | Signals | Response |
| --- | --- | --- |
| Lyssa, center (-144, 49) | Frostbite 25175; Punishing Slice 25177 | Forecast the emergence from footprint records, then replace it with the actual 51-by-50.5 forward rectangle. Hold the native-selected safe exit through resolution. |
| Lyssa | Ice Pillar 25179; Pillar Pierce 25375 | Pillar circles of radius 4.5; rays of width 5 and length 80.5, clipped by the room boundary. |
| Lyssa | Heavy Smash 25180 overlapping pillar rays | Join the actual stack target at a safe point; let earlier rays resolve when no shared safe point exists. |
| Ladon, center (0, 48) | Head statuses 2812–2814; breaths 25734–25739 | Reserve padded 120-degree, radius-40 sectors for the casting head and the charged second head, which exhales 2.1 seconds later. |
| Ladon | Pyric Sphere damage helper 25745 | Crosses with width-5 arms extending 50.5 from the helper. The visual cast 25744 does not own damage timing. |
| Ladon overlaps | Sphere, later breath and Pyric Blast 25742 | Keep the earlier sphere active before enabling later breath sectors. For stacks, use the actual marker target or bring a self-carried marker into the party. |
| Hermes, center (0, -50) | True Aero IV 25889; Quadruple lanes 27837 | Width-11, length-50.5 lanes. For Quadruple, expose the earliest four lanes and advance after their effects instead of treating all future lanes as simultaneous. |
| Hermes | Meteor 25890; Cosmic Kiss 25891 | Stage at center through proximity impacts; radius-10.5 meteor exclusions remain active. |
| Hermes | Double Hermetica 25893; meteor-blocked volley 27836 | Identify the intact meteor, shelter downwind and rotate around that same rock for the second wave. |
| Hermes | True Aero bait 25899; repeat 25901 | Separate from party-directed rays, then avoid the locked width-7, length-40.5 ground repeat. |
| Hermes | True Aero II 25896/25897; repeat 25898 | Separate from allies using radius-6.5 circles, then leave the locked ground circles. Never register a moving self-centered spread avoid. |
| Hermes | True Tornado 25902; repeat 25906 | Separate the radius-4.5 targeted buster from allies and avoid its ground repeat. |
| Hermes | True Bravery 25907 | Submit an available class/job interrupt, with native readiness checks and one accepted request per cast. |

Tankbusters and raidwides remain routine-owned. Hidden helpers must remain in the
actor collection even when untargetable. Real bosses are identified by BaseId:
Lyssa 0x3323, Ladon 0x3425 and Hermes 0x348A. Hidden damage helpers can reuse a
boss NameID, so NameID alone is insufficient.

### Stacks and effect timing

A stack requires participants, not just arrival at its center. When the marker
is on self, candidates maximize living party coverage and retain an equally good
destination as allies settle. Candidate positions allow 4.5 yalms to participants
within the six-yalm stack radius, reserving one yalm for arrival tolerance and
0.5 for clearance. The self-stack planner samples the approach every half yalm
to reject paths across earlier sphere lanes. Missing participants or an unsafe
approach release owned movement and allow another attempt on a later tick.
Party positions are used for confirmed stack participation, never as generic
mechanic-follow guidance.

Captured damage arrived after the reported cast-bar finish: 0.78 seconds for
spheres, 1.13 for breaths, about 1.1 for Pyric Blast and 0.90–0.95 for ordinary
Aero IV. Those mechanics retain a 1.5-second effect tail. Other hazards retain
0.5 seconds; extending queued Quadruple lanes would block the next safe lane.

Ladon's body Heading does not include the casting head's angle. Status 2812 is
front, 2813 adds 120 degrees and 2814 subtracts 120 degrees. Charged head IDs
survive aura disappearance until the sequence finishes. During sphere/breath
overlaps, the later breath is deferred until the sphere tail ends, leaving more
than four seconds for the observed breath escape.

### Footprints and meteor signals

Global, CN and TC `bg.lgb` assets place Lyssa's footprint objects at IDs 8752181
and 8752182. States 0x1, 0x10 and 0x80 select headings 0/60/120 degrees on the
first object and 300/180/240 on the second. Emergence origins lie 12.5 yalms from
the room center. Actual Punishing Slice casts supersede the forecast and log
origin/heading error. Missing forecasts still leave the actual-cast warning.

After native avoidance reaches the safe crescent, the Frostbite hold prevents
combat movement from approaching the underground boss and entering the slice
again. It checks both the current forecast rectangle and native avoids because
the native collection can lag the new forecast by one pulse.

Meteor actors use BaseId 0x348C. Select the unique intact state 0 only when all
four actors are readable and the other three report cracked state 1; a sole
readable survivor also supports late attachment. Unknown state is never intact.
Retain the selected identity and scalar position through cracking/disappearance
so the second volley uses the same shelter. Snapshot each gate once: completed
helpers can move to the next wave before the previous effect resolves.

Each wave retains its heading through cast finish plus 0.5 seconds. Shelter is
3.8 yalms downwind of the 2.4-yalm blocker, leaving 1.4 yalms of lateral margin
after arrival tolerance. Before a volley appears, stage on the room-facing side
after proximity damage ends. Reject points outside the inset arena or on the
source-facing side. Ambiguous signals yield without choosing an NPC fallback.

The meteor animation reader uses a semantic character-state copy sequence,
validated with Ghidra/Sleigh operand metadata. All displacement operands are
wildcarded; exactly one match is required. Hexadecimal `Add 3/14 Read32` extracts
adjacent source fields, and `Add D/1A Read32` must reproduce the same destination
fields. Runtime checks also bound the first field to 0x900–0x1200 before reading
the second byte. The extracted offset is cached per process identity.

| Executable checked | SHA-256 prefix | Unique .text match VA | Source/destination fields |
| --- | --- | --- | --- |
| Installed Global | 46A52AB1DBE80B9A | 1408D36DE | CF1/CF2 |
| Installed CN | 7BA28760BC53CBBF | 1408D18FE | CF1/CF2 |
| Installed TC | 9FB8DD46FDE74BF1 | 14088D18E | CE1/CE2 |
| Prior Global | 9483706DDCCC700F | 1408D22FE | CF1/CF2 |

These checks validate binary layout, not regional gameplay. No Korean client was
available. Observed offsets are evidence only; runtime derives the attached
client's field instead of assuming a regional constant.

### True Bravery interruption

Magitek's available helper was internal and stun-first, and its global queue did
not bind a request to this cast's lifetime. The handler therefore uses RB's public
ActionManager without changing routine settings or loading its collectible assembly.

| Class/job | Interrupt |
| --- | --- |
| Gladiator, Paladin, Marauder, Warrior, Dark Knight, Gunbreaker | Interject 7538 |
| Archer, Bard, Machinist, Dancer | Head Graze 7551 |
| Blue Mage, when available | Flying Sardine 11423 |
| Healers, melee and other casters | None; yield to the routine |

`HasSpell` and native `CanCast` gate dispatch. The handler neither cancels the
player's cast nor changes target, facing or position to interrupt. Native escape
retains priority. Rejected requests retry at most every 250 ms; one accepted
submission consumes its tick and remains latched until Hermes ends the cast.
The Blue Mage mapping does not bypass duty eligibility or spell availability.

## Verification record

All times below are UTC on October 27, 2026. Live runs used the Global client and
Duty Support through the existing protected profile. A clear after recovery is
recorded separately from a clean mechanic result.

| Build / run | Log filename | Outcome and relevant evidence |
| --- | --- | --- |
| .8, Paladin | `61496 2026-10-27 11.28.txt` | Interject at 15:49:36.122 and 15:50:57.176. The second cast ended at least 3.3 seconds early with unchanged auras; normal combat resumed. This verifies interruption, not just action submission. |
| .9, Paladin | `76644 2026-10-27 12.26.txt` | First fully non-following implementation. One sphere vulnerability, no deaths. Frostbite caused 9, 10 and 15 extra escapes after reaching safety. These failures led to ordered breath activation and the safe hold. |
| .10, Paladin | `29492 2026-10-27 12.53.txt` | Full clear with zero vulnerabilities/deaths. Three Frostbites had no extra escapes; the sphere/breath overlap and both meteor sequences passed. Interject worked twice. A brief initial hold exposed the native-avoid cache delay, corrected in .11. |
| .11, Viper | `60240 2026-10-27 13.21.txt` | Three stable Frostbites. Self-targeted Pyric Blast killed Arelia at 17:38:27.675 while allies waited about 10 yalms away. Automatic recovery completed the duty; zero vulnerabilities, one death. This exposed the incorrect self-stack assumption. |
| .12, Viper run 1 | `74352 2026-10-27 13.52.txt` | Full clear at 18:18:43.419 with zero vulnerabilities/deaths. Stable Frostbites, other-target stacks and both meteor sequences passed. |
| .12, Viper run 2 | Same log, after first-run stop | Native lane escape finished at 18:41:57.875, then Slither crossed live lanes at 18:41:58.609. A retry repeated it. Two vulnerability-gain events, three gained stacks total and one death; recovered clear. This led to the GapCloser lease and ordinary-lane effect tail. |
| .13, Viper retest | `69296 2026-10-27 14.47.txt` | Full clear, leaving at 19:14:52.028. Zero recorded vulnerabilities/deaths. The previously failed lane pattern, three stable Frostbites, Ladon overlaps and both meteor sequences passed. |

The .13 Hermes layout had helpers at (22,-45), (22,-55), (5,-72) and (-5,-72).
Casts began at 19:11:05.500; native escape completed at 19:11:08.351. Uncoiled
Fury ran immediately afterward while GapCloser stayed blocked until 19:11:10.930.
No Slither crossed live lanes. Slither worked later at 19:11:26.800 after the gate
released. A stopped-host integration probe also confirmed the deployed Magitek
`CanUseGapCloser` changed from true to false during the lease and back to true
on release, without issuing an action or movement.

Every final-run footprint forecast matched its actual helper with zero origin
and heading error. Each Frostbite used one hold and no repeated escapes. The
19:04:27.144 Pyric Blast selected Venat. Neither .12 run nor the .13 retest
selected Arelia for the corrected self-stack branch, so live self-target coverage
remains open. Other interrupt jobs and regional gameplay are also unverified.

The isolated harness at `%LOCALAPPDATA%/Temp/KtisisMechanicChecks` links all three
production files with fake frame services. Its 102 checks cover geometry,
phase ordering, forecasts, ambiguous meteor state, shelter transitions, stack
participation, delayed impacts, interrupt mappings, native handoffs and lifecycle
cleanup. The self-stack replay reproduces the failed layout and verifies movement
into all three allies' range after the blocking sphere lanes expire. These checks
supplement the live records; they do not establish native geometry or timing.

The final developer build retained diagnostic collection. Arelia stopped outside
the duty on Viper 100 at 19:14:56.928, with Movement and GapCloser unblocked,
SideStep and Platypus Hooks enabled, and both LlamaMarket hooks disabled. The
completion watcher exited. No desktop controls were used.

### Deployed revision identity

The live .13 capture used the following SHA-256 hashes before the comment and
formatting cleanup. The cleanup preserves executable tokens after normalizing
statement braces; these hashes identify the tested package, not the cleaned files.

| File | SHA-256 |
| --- | --- |
| DutyMechanic DLL | `3B0BB69FD867F054AC8EE81285ABE09CCD015FEF481D713723FAA7CD3D091A96` |
| Main handler | `400E8E6209A500A2302EE308867316F2C075766203D37576D165FF52439FB15A` |
| Geometry | `F97D7C447B5ABD64DC790718BEAD145A0B1AEC401749713311F3CA0D40790224` |
| Signals | `B79A89083099AF53D87323785C603842C2CFEBD6E9F31373228BE3A1B3657DD9` |

Logs are under `Reborn - Crucible-Arelia/Logs`. The .12 first-run log was copied
to `%LOCALAPPDATA%/Temp/KtisisDataProbe/V12-Viper-Run1.txt` before the second run.
The .13 rollback is under that installation's
`Plugin Rollbacks/DutyMechanic/7.5.4282015.12-20261027-144755-5b573f42`.

## References

The implementation review compared geometry and mechanic semantics against
[BossMod](https://github.com/awgil/ffxiv_bossmod/tree/7be17d76f1cbbe0afb728c4faaf63fa7810ba260/BossMod.Modules/Endwalker/Dungeon/D04KtisisHyperboreia)
and [BossmodReborn](https://github.com/FFXIV-CombatReborn/BossmodReborn/tree/ae64e3309905dafad0952dbe72680add167591f9/BossMod/Modules/Endwalker/Dungeon/D04KtisisHyperboreia).
The [Lyssa](https://www.gamerguides.com/final-fantasy-xiv-endwalker/guide/dungeons/ktisis-hyperboreia/lyssa-boss-ktisis-hyperboreia),
[Ladon Lord](https://www.gamerguides.com/final-fantasy-xiv-endwalker/guide/dungeons/ktisis-hyperboreia/ladon-lord-boss-ktisis-hyperboreia)
and [Hermes](https://www.gamerguides.com/final-fantasy-xiv-endwalker/guide/dungeons/ktisis-hyperboreia/hermes-boss-ktisis-hyperboreia)
guides corroborated the player response. Reference agreement alone was not treated
as gameplay verification.

Interrupt roles were checked against the official
[interrupt guide](https://na.finalfantasyxiv.com/uiguide/battle/battle-target/ecast_interrupted.html),
[Paladin](https://na.finalfantasyxiv.com/jobguide/paladin/) and
[Bard](https://na.finalfantasyxiv.com/jobguide/bard/) tables. Action IDs agree with
Magitek's `Utilities/Spells.cs`; Flying Sardine row 11423 was checked in the
installed game's Action sheet.
