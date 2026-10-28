# Thornmarch (Hard)

The handler supports the reworked trial in territory 1067, ContentFinderCondition
66. The standalone profile owns queueing, arena arrival, guard priority, loot and
return to its caller. The combat routine retains job actions and ordinary combat.
These notes preserve the constraints established by the October 2026 captures.

## Encounter ownership

| Mechanic | Response |
| --- | --- |
| Spinning Mogshield, 29216 | Leave the six-yalm circle. |
| 1000-kuponze Swipe, 29215 | Avoid the 20-yalm, 90-degree frontal cone. |
| Moggleday Night Fever, 29213 | Avoid the 30-yalm, 120-degree cone; installed cast type 13 corroborates the cone interpretation. |
| Mograin of Death, 29192 | Separate from other marked players; never register the controlled player's own marker as a harmful circle around itself. |
| Pom Meteor, 29193 | Fill uncovered five-yalm towers, preferring healer/ranged assignments and respecting actual occupants. |
| Pom Holy and Memento Moogle | Keep healing and mitigation schedulable; raidwide damage has no escape geometry. |
| Pom Bog, 29207 | Avoid the eight-yalm cast and persistent event object. Route positive positioning around the bog. |
| Mog Stone IV, 29204 | Join the marked ally. A self-marked character joins living participants rather than holding alone. Resolve this stack before the later spread. |
| Moogle-Go-Round, 29196–29197 | Avoid the first two staggered circles, stage for the next safe region, then advance with the cast sequence. |
| Twin Pom Meteor, 29206 | Tanks share the marked tank's hit; other roles avoid it. |
| Third Decree | Bait three comets away from towers before soaking. Process Pom Stone's earliest ring cohort and release the stack before Mog Creation's line. |

Damaging geometry has a half-yalm margin. The circular arena is inset to 20.5
yalms. Native avoidance receives the current stage's hazards; SideStep is disabled
for this encounter to avoid duplicate or conflicting geometry and restored on exit
or stop. Stable scalar envelopes preserve native avoid identity while actors move.

Positive stack and tower positioning yields to native escape and uses the existing
mover and movement capability. Once positioned, it returns control so healing and
rotation can run. Loading, death, actor disappearance, stop and instance exit clear
transient requests. The handler does not choose combat targets or job rotations.

## Tower state and assignment

The live indicators are the `b1922` placements 9284371, 9284374–9284380. Colocated
`mogmete01` decorations remain inactive and must not be used as tower evidence.
Native state `1` means active/unoccupied, `0x10` active/occupied, and `4` inactive.
These native values differ from full network map-effect states.

An observed Meteor or Decree cast arms new indicators. Initial towers use an
eight-second deadline; the later comet sequence uses sixteen seconds. Occupancy
changes preserve the first deadline. Unknown states and inactive indicators release
the tower, while an unarmed active observation cannot create a stale goal.

Actual occupants claim their towers before role-based assignment. Once our character
accepts a reachable uncovered tower, another player's predicted destination cannot
cancel that leg. Keep it until actual coverage, disappearance or loss of reachability;
an earlier required stack still takes precedence. This prevents the captured west-
tower abandonment when a moving bard changed the hypothetical party assignments.

## Profile constraints

Use the reworked king's live name ID 725, with the seven guard IDs 718–724. The
older king name ID 2752 has the same display text but did not identify the captured
king. Synchronized non-tanks wait for the group; undersized carries can pull on any
job. Loading the profile inside an already queued instance preserves that mode.

Ordinary arena arrival precedes grinding, after the party-pull wait. Ranged jobs
otherwise remain in the entry pad, where RB 1.0.941 can retain the opened barrier
in local avoidance collision. See [the collision report](ThornmarchNavigationIssue.md).
No off-mesh route or replacement movement controller is needed.

## Verification boundary

The original Paladin test cleared with no deaths or vulnerability gains and observed
shared-tank positioning with routine actions during the hold. White Mage captures
verified self/other stacks, tower occupancy, healing during a tower hold and normal
exit. The latest assignment-retention change passes 39 planner checks, including a
replay of the failed west-tower capture. The full build has zero errors and thirteen
existing warnings outside these changes.

The final combined candidate passed White Mage and Paladin runs on October 28.
Both recorded zero deaths, vulnerability gains, failed escape searches and observer
errors, followed by normal loot and exit. White Mage reached and held its tower,
cast Regen during the hold, and released on indicator cleanup. Paladin left covered
towers alone and joined the marked Gunbreaker within 5.15 yalms for Twin Pom Meteor,
with Riot Blade and Fight or Flight still scheduled during the hold. Post-exit
inspection confirmed SlideMover, SideStep enabled and no temporary sample callbacks.

This meets Domestic's release criteria, which allow one vulnerability hit and do not
require repeatedly forcing normally skipped late mechanics. Late comet/ring/line
execution, arbitrary long routine waits and every recovery variation remain outside
the live coverage. Tests used an external copy that stops after normal duty exit;
the production profile retains its unchanged LastProfile handoff. The local audit
records exact source hashes and per-run evidence separately.

## Release 7.5.4282015.16

This source release registers the reworked trial and publishes the tested tower,
stack and avoidance handling alongside the corrected standalone profile. The version
bump lets existing installations identify the Thornmarch update.

Cleanup expands compact control flow and fields and keeps maintenance comments beside
the relevant state and movement rules. Roslyn token comparison against the final
live-tested sources permits only formatting, comments and split field declarations.
Expanded XML comparison preserves every profile action, attribute and its order.
Both comparisons pass, as do all 39 planner checks and the Release build (zero errors,
13 existing warnings). The cleaned revision has not had an additional live run.

The shared diagnostic facility remains intact with its published switch off. Every
changed file was reviewed for durable rationale; raw captures and temporary test
profiles remain outside the published source.
