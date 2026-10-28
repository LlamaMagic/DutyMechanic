# The Striking Tree (Hard)

The handler supplies Thunderstorm rescues and Rolling Thunder pickups for territory
374 (ContentFinderCondition 77). The standalone trial profile owns queueing, entry,
Arbiter priority, loot and exit. These notes preserve the constraints behind the
October 28, 2026 implementation and distinguish tested behavior from open coverage.

## Mechanic ownership

| Mechanic | Response and owner |
| --- | --- |
| Shock Strike (2274) | Native avoidance keeps other players 5.5 yalms from the last living boss victim, including a 0.5-yalm margin. |
| Thunderspark (2275) | SideStep retains the observed reach-adjusted 7.2-yalm circle. |
| Thunderstorm (2277/2278) | DutyMechanic registers ground and player circles at 5.5 yalms. A marked player approaches an uncovered Terror victim within 3.5 yalms. |
| Chaotic Strike / Terror (2280 / status 66) | Rescue selection excludes victims already covered by another marked, mobile party member within four yalms. |
| Grey Arbiters | The profile prioritizes name ID 2833 over Ramuh 2832. SideStep handles Thunderbolt (2282), a 48-by-4-yalm line. |
| Judgment Bolt (2279) | The combat routine retains healing and mitigation; the raidwide is not an avoidance region. |
| Rolling Thunder / Causality (2281 / status 514) | Suppress actions while linked and collect visible lightning objects (0x1E948A). Conductivity (512) stacks determine remaining pickups; Protection (517) prevents a fourth pickup. |

The live helper family is 2274–2282. Similarly named Extreme actions are not used.
Installed game data and live captures corroborate the
[Thonky guide](https://www.thonky.com/final-fantasy-xiv/duties/the-striking-tree-hard)
and [community guide](https://ffxiv.consolegameswiki.com/wiki/The_Striking_Tree_(Hard)).
No matching Hard-mode module was present in the reviewed BossMod revisions
`4707e49c27caabc56a52a21826cc46272c09fbf1` and
`ae64e3309905dafad0952dbe72680add167591f9`; other Ramuh encounters were not substitutes.

## Movement and scheduling

One encounter-local gate arbitrates native escape, mechanic positioning and ordinary
combat through the existing SlideMover and navigation provider. Unknown movers are
left alone. A successor command revokes the previous input's cleanup claim; detached
callbacks cannot stop a replacement controller. Stop, loading, death, exit and owner
replacement release the gate and its capability leases.

Native escape preempts rescue and orb travel. Positive positioning resumes only after
300 milliseconds continuously clear, preventing repeated travel/escape switching near
moving markers. Rescue travel retains scheduling because an awaited routine cast can
outlast the marker window. Once positioned, healing resumes under the movement hold.
Causality is the exception: actions remain suppressed until either partner clears it.

Orb selection begins with partial sets rather than waiting for three to coexist.
Actual aura stacks and visible objects drive each new leg. A pickup with no progress
for three seconds is excluded for four seconds so another available route can proceed.
No job rotation or cooldown selection is implemented here.

Native avoids use scalar centers to avoid RB's implicit combat-range preference for
the first BattleCharacter in a collection. The arena leash is 24.5 yalms, half a yalm
inside the observed perimeter. The profile stages outside the small entry area using
ordinary mesh navigation: RB can retain the opened barrier in avoidance collision.
See [the collision report](StrikingTreeNavigationIssue.md); staging is a mitigation,
not a repair to native collision extraction.

## Verification and release

Domestic accepted the White Mage run ending at 20:06:03 UTC on October 28, with exit
at 20:06:29. The run recorded zero player deaths, no-path failures or encounter
exceptions. Two Terror rescues succeeded, including an approach after the settling
hold. Three orb pickups removed Causality and routine actions resumed. Entry staging
also passed while the native collision sample still contained the closed barrier.
Post-exit inspection confirmed the original mover restored and no navigation wrapper
or temporary observer remaining. Domestic reported smooth movement with nothing lacking.

Earlier Viper runs verified rescue and completion, but the final settling revision was
accepted on White Mage only. Tank-role execution, every obstructed rescue route and
the scarce partial-orb gameplay case remain outside that acceptance. Partial sets and
movement handoffs are covered by 28 deterministic checks. These limits are not erased
by a party clear.

Release cleanup changes layout and maintenance comments without changing encounter
decisions or profile actions. Roslyn syntax comparison checks the three source files
against the accepted snapshot, allowing only split field declarations. Expanded XML
comparison checks action attributes and order. All comparisons passed. The combined
Release build passed with zero errors and 13 existing warnings; all 28 selection and
ownership checks passed. No additional live run is claimed for the formatting
revision. The default-off shared diagnostic facility and its encounter signal
sampling are retained. Every changed file was reviewed for documented rationale.
