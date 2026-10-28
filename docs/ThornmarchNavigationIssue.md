# Thornmarch entry collision

RB 1.0.941 reports the opened entry barrier as local avoidance collision in
Thornmarch (Hard), territory 1067. A ranged character can remain on the entry pad
while attacking or healing, then fail to find an escape from an overlapping spread
or cone. The standalone profile completes ordinary mesh navigation to its verified
arena center before grinding to avoid starting combat movement in that pocket.
This mitigates the collision defect; it does not repair the collision extractor.

## Reproduction and evidence

Global client 2026.10.16, October 28 White Mage tests:

- Run 2 produced 191 failed escape searches near `(0, 0, 15)` during the opening
  spread and Moggleday Night Fever. Run 3 reproduced 74 opening failures.
- A fixed collision volume centered at zero, with half-extents `(70, 40, 70)`,
  returned 9,713 triangles before opening, at opening, and 2, 5 and 10 seconds
  afterward. Its order-independent fingerprint remained `CC84C6F23ADC7966`.
- The native avoidance heightfield rebuilt after opening, but its escape searches
  still returned zero spans. Removing the encounter leash restriction did not help.
- The captured collision includes 32 vertical entry-wall triangles bounded by
  X `[-3.2359054, 3.2640946]`, Y `[-2, 6]`, Z `[11.64947, 18.149471]`.
- Arelia physically crossed those bounds to the south tower at `(0, 0, 10)` and
  held it successfully. The collision is therefore inconsistent with the open
  passage available to the character.

A detached replay rebuilt native heightfields from the captured triangles using
the installed runtime's cell and agent settings. With a 6.5-yalm circle centered
on the test position and a 20.5-yalm arena leash, the entry position
`(0.1021, 0, 14.9375)` produced zero escape spans; `(0, 0, 10)` produced 16.
Replay never replaced the live heightfield or submitted movement.

Local evidence is in the October 28 Live Trials `Thornmarch` capture directory:
`Arelia-healer-towers-run3.log`, `run3-collision.json`, `inspect-collision.py` and
`replay-collision.cs`. Runtime product version:
`1.0.941+d62fa4f76bf25e37fc3f442c2b86a2b49124d03a`.

## Repair boundary

Investigate why `GameWorld.GetTriangles(..., HitBoundaries)` includes the disabled
entry barrier. This is not evidence of a broken service-navigation mesh edge, so
no jump link or off-mesh crossing is proposed. Repeated `ResetNavigation()` calls
would rebuild from the same retained triangles and discard active escape paths.

The profile uses the existing arena-center arrival, after its party-pull wait,
through ordinary `MoveTo` with combat and avoidance still enabled. No custom
movement controller or coordinate-based progression is introduced. Gameplay
verification of that profile correction is recorded in the encounter audit.
This report has not been submitted to the RebornBuddy developers.
