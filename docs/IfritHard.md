# The Bowl of Embers (Hard)

The territory 292 handler supports synchronized parties with the standalone trial
profile. The profile prioritizes infernal nails and owns entry, combat targets,
completion and exit. The combat routine remains responsible for job actions.

Ifrit's instant frontal attacks exclude the player holding his aggro. Crimson
Cyclone uses a 43-by-12-yalm rectangle because Global build 2026.10.16 supplies an
empty omen matrix for the charge. Both shapes include a small safety margin.
SideStep retains Eruption and Radiant Plume; its Cyclone override is released on
exit. An encounter-scoped mover adapter releases completed native escape input
without stopping a successor combat or navigation request.

## Verification

October 28 synchronized matched-party captures covered a Paladin baseline, a White
Mage candidate and the final Paladin candidate. All cleared without a recorded
player death. The final candidate escaped all three charges before impact, retained
nail priority and ordinary combat, and restored the original mover after exit.
Eruptions, outer/inner Plumes and Hellfire were observed. Twelve movement-ownership
checks cover completion, interruption, overlapping hazards and successor handoffs.

The final charge repair was tested on Paladin; the healer capture predates that
repair. No dedicated DPS/off-tank or wipe/re-entry test was performed. Parties killed
Ifrit before the late broad Plume pattern and repeated charge cycles. Charge
avoidance starts at the observed cast, without predicting from animation timelines.
These gaps remain explicit rather than forcing slower or undersized tests.

Release cleanup preserves the tested C# logic and expanded profile actions. Build,
movement checks and diagnostic release invariants are checked separately from live
gameplay; formatting cleanup is not claimed as another gameplay run.
