# The Howling Eye (Hard)

The territory 294 handler supplies pillar cover for Mistral Song and Shriek,
Slipstream cones, persistent tornado footprints and Eye of the Storm's inner safe
area. Cover uses surviving monoliths and must satisfy every concurrent shelter
cast and native hazard. A single movement gate gives native escape priority over
cover, then returns control to normal travel. Healing remains available while held.

The profile defaults to synchronized matchmaking and prioritizes plumes and sisters.
The handler adjusts eligible targets for sister tethers and tank assignments without
introducing extra combat targets. The arena center is the fallback when targets
disappear. Completion, loot and exit remain in the standalone profile.

## Verification

October 28 local testing used Arelia in ordinary eight-player matched parties on
Global build 2026.10.16 / RB 1.0.941. A Paladin baseline exposed missing Song cover
and repeated entry into tornadoes between pulses. Two White Mage candidate clears
then reached different pillar shadows before Song resolved, with no Song damage
or recorded player death. Slipstream, plumes, Aerial Blast and recurring tornadoes
were observed. The second candidate also verified the navigation-query repair:
GrindSafe can query the original mesh provider while movement is gated.

Twenty-three production-linked shelter and input checks cover overlapping geometry,
stale navigation callbacks, forced movement and foreign-owner cleanup. These checks
do not replace gameplay evidence. Release builds retain disabled mechanic capture.

Shriek, Eye of the Storm, live green/red tether selection, a dedicated DPS/off-tank
run and wipe/re-entry remain unverified. The matched groups did not expose those
late mechanics sufficiently; no undersized or deliberately slowed tests were used.
The Paladin run predates the handler and is baseline evidence, not tank acceptance.
