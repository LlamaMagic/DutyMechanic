# The Chrysalis

This handler supports Nabriales in territory 426, duty-finder condition 84. The
standalone profile defaults to synchronized matchmaking and owns entry, room-specific
targets, wipe recovery, completion and exit. Non-tanks wait for the party pull.
These notes preserve the observed constraints and distinguish live coverage from
mechanics that a matched group did not reach.

## Mechanic ownership

- Orb collection follows physical and magical vulnerability statuses 657 and 658.
  The handler retains a safe nearby orb, leaves occupied pickups to other players,
  and allows healers casting windows between collections. Resolved caster IDs stop
  a completed pickup from becoming a new avoidance hazard before its actor despawns.
- Blight (659) delays portal entry even when the boss disappears and combat drops.
  A fixed outward destination prevents sideways corrections during the pull. The
  destination sits at 23.9 yalms because normalizing exactly to the 24-yalm travel
  boundary can round outside the floor check.
- Extend (660) identifies the meteor room. Tanks take uncovered meteor objects
  (2005159); occupied towers are left to their current player. A conservative center
  check avoids claiming that an unmeasured outer radius is safe. Helper relocation
  releases a resolved meteor before its visual object disappears.
- Healers position near the meteor-room center while retaining routine healing.
  The profile prioritizes the Aetherial Tear and returns to Nabriales after the rift.
- Both rooms share a territory. Native avoidance can retain stale collision after
  teleporting, so the handler refreshes it after settling and retries a stalled
  escape at bounded intervals. This recovery does not depend on a combat target.

One borrowed movement owner arbitrates mechanic travel, native avoidance and ordinary
navigation. Its provider gate prevents navigation anti-stuck input from competing
with a mechanic request. Reachability queries remain available, and stale asynchronous
callbacks cannot stop a successor request. Stop, death, loading, exit and plugin
disable release borrowed state without replacing a successor's controller.

## Acceptance and coverage limits

Synchronized eight-player runs exercised White Mage healing/positioning and Paladin
orb collection, portal timing and meteor soaking. The accepted healer pull cleared
without the prior return-floor escape failure. The final candidate then inset the
portal destination by 0.1 yalm; that adjustment was exercised on Paladin, without a
separate final-revision White Mage run.

The final Paladin run cleared at 01:10:12 UTC on October 29, 2026 (October 28 locally),
left at 01:10:24 and stopped outside at 01:10:29. It recorded no deaths, repeated
vulnerability stacks, encounter exceptions, failed collision-span lookups or
anti-stuck movement. Blight expired before entry. The other tank covered the first
meteor; Arelia covered the next two within half a yalm of their centers. After returning,
native avoidance escaped End of Days and collision queries succeeded on the main floor.

There was no separate DPS-job run. Later orb waves and a full meteor timeout/enrage
were not forced by slowing a live group. Domestic accepts natural matched-party
coverage with skipped mechanics documented; acceptance does not imply those cases
were observed.

## Release 7.5.4282015.18

The release adds the handler, territory registration and lifecycle cleanup alongside
the matching DWsWebserver profile. Maintenance comments explain movement ownership,
collision recovery and observed timing bounds. Raw logs and diagnostic harnesses
remain outside the release; the shared diagnostic facility remains available with
its production switch disabled.

Normalized C# syntax and expanded XML match the accepted candidate after cleanup.
All 64 policy/movement checks pass. The Release build has zero errors and 13 existing
warnings. Every released file was reviewed for its maintenance rationale. The cleaned
source was not deployed for another gameplay run.
