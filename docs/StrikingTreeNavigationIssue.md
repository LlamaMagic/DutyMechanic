# Striking Tree entry collision discrepancy

Prepared for Domestic to report to RB's developer; not submitted. This records the
native geometry failure so the profile staging move is not mistaken for a mesh fix.

## Reproduction

- The Striking Tree (Hard), territory 374 / CFC 77, matched party, White Mage.
- Global client `2026.10.16.0000.0000`; installed RB API `1.0.9037-rc.1`.
- October 28, 2026, all times below UTC. Evidence: `Arelia-healer-revision6b.log`.
- LLJoinDuty waits for the initial barrier to open. Arelia remains at
  `(-0.791,74.700,16.616)` while waiting for the party to pull.
- At 19:32:37, a five-yalm Shock Strike safety circle covers that position. Native
  avoidance cannot generate an escape. Thunderstorm overlaps shortly afterward.
- Eight bounded read-only probes return zero path spans with both the explicit
  `(0,75,0)/24.5` arena leash and an unrestricted native search. Removing the leash
  therefore does not explain this reproduction. No competing movement was issued.
- The run records 486 `Couldn't generate a path out of avoid` messages. It later
  clears at 19:36:05 and exits at 19:36:31; a clear does not validate the opener.
- Earlier Viper revision 5 reproduced 52 opening failures before the collision
  observer existed. The discrepancy is not inferred solely from instrumented runs.

## Collision evidence

`GameWorld.GetTriangles(center ± (70,40,70), RaycastFlags.HitBoundaries)` at fixed
center `(0,75,0)` returns 9,159 triangles before entry opens, at the director timer's
opening edge, and at +2/+5/+10 seconds. The order-independent vertex fingerprint is
`CA401ADA1A12C97B`. A later sample during combat returns the same triangle count.

The saved `revision6b-collision.json` includes a closed double octagonal wall around
spawn: outer X ±3.25, Z 14.26–20.76; inner X ±2.75, Z 14.76–20.26; Y 72.99–80.99.
These 32 vertical triangles enclose Arelia's initial position. The prior clean run
returned 9,127 triangles and fingerprint `9698FADDAFB8E609` over the same volume.
The paired `revision7-collision.json` contains exactly the same remaining triangles:
32 failed-state-only triangles and zero clean-state-only triangles after comparing
unordered vertex triples. The differing bounds are X ±3.25, Y 72.99258–80.99258,
Z 14.263439–20.76344. This confirms the geometric difference; the owning client
object and the reason for its inconsistent inclusion remain unresolved.

The old outside-zone heightfield is replaced after entry. By 19:32:38, the field
has 17,373 spans and bounds `(-70.791,34.700,-53.384)` to `(69.209,114.700,86.616)`.
Failures continue on this current-zone field. Arelia later crosses out of the spawn
region through the existing navigation/mover path, without off-mesh input from the
diagnostic. Native triangle extraction and physical traversal therefore disagree.

Do not add a repeated ResetNavigation retry: the current collision sample still
contains the wall, so rebuilding from it is not an established repair. No native
offset, collision mask, triangle filter, shared avoidance code or mover is changed.

## Profile mitigation and verification

The profile uses the supported mesh MoveTo to `(0,75,10)`, tolerance 1.5, only while
inside the small spawn rectangle after the loading/death/cutscene guard. It stages
outside the enclosed region before the matched non-tank waits for the party pull.
This is ordinary route staging, not an off-mesh connection or a collision repair.
Production loot/exit/LastProfile and normal combat/death handling remain in place.

External test profile SHA256:
`DA9AB8E9F7D24F17F9DF3A47993A63C2D70013BCD9ECB2DF0C090D88A64C851E`.
The first profile test used mechanic revision 6. Its staged entry reached
(-0.172,74.992,11.343) by 19:38:38 with no opening failure, but its collision sample
dropped from 9,159 to 9,127 triangles when the barrier opened. It therefore validates
ordinary staging, not staging through the retained-wall condition. Do not claim
the intermittent defect is fixed from this healthy geometry case.

The detached replay (`replay-collision.cs`, `collision-replay-result.txt`) rebuilds
private copies from the paired saved triangles using the installed native settings.
The captured spawn/victim circle produces zero escape spans with the extra wall
and seven with healthy geometry. A five-and-a-half-yalm circle centered on the
staging position produces 14 escape spans with either geometry. These checks use
the real native planner but never assign its copied fields or paths to live state.
They corroborate the obstruction and the value of staging; they do not prove that
the live opening move always completes before an overlapping mechanic begins.

The retained-wall case subsequently passed live with the same profile and mechanic
revision 8 (`Arelia-healer-settling-revision8.log`). At 20:02:35 the barrier timer
opened but the 9,159-triangle fingerprint remained; it was still present at +2,
+5 and +10 seconds. Arelia started inside the ring at (-0.020,74.700,15.630), began
MoveTo at 20:02:36.293, reached (-0.007,74.951,11.965) by 20:02:37.208, and entered
Grind at 20:02:38.440. No opening no-path failure was recorded. Revision 8 only
adds a settling interval after escape before rescue/orb positioning; it does not
alter opening geometry, staging or native avoidance. This validates the mitigation
against one observed retained-wall entry, while the underlying RB discrepancy
remains open for developer investigation.

## Evidence location

Domestic retains the private captures and diagnostic source in the October 28
StrikingTree evidence bundle. The filenames above identify the relevant records.
The observers use the existing bot thread, record bounded samples, issue no input,
and unsubscribe at bot stop. The triangle JSON is diagnostic output; its capture
motivation and interpretation limits are documented here rather than inside JSON.
