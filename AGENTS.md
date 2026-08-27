# DutyMechanic maintenance constraints

## Yuweyawata Field Station

### Soulweave origin

The 2026-08-26 capture established that Soulweave's raw cast-type-10 helpers expose zero `CastLocation` and `AVFX.Center` values. Their `OmenMatrix.Center` is the actual 28-to-32-yalm donut origin and is exactly 30 yalms from the Preserved Soul in the observed waves. Snapshot the validated omen center at cast discovery. If it is not yet populated, derive the same origin from the helper as `(X + cos(heading) * 30, Z - sin(heading) * 30)`; never substitute the helper's own location as the ring origin.

### Protected Necrohazard handling

Overseer Kanilokka's Necrohazard implementation is protected by live evidence. Preserve its detection, map-effect layout selection, forced-movement input gating, route construction, fallback behavior, timing, priorities, and lifecycle cleanup exactly as implemented.

The 2026-08-26 ThreeRoutes capture identified and explicitly authorized one repair: an early Trust-following pulse can leave the player about 0.10 yalms beyond the conservative five-yalm center island just before the exact layout arrives. Exact-route construction snaps that position to a valid entry cell, so the input gate permits a bounded one-yalm unmodeled prefix only while approaching that nearby exact-route waypoint and only when the remainder stays on verified floor. Preserve those recovery bounds; do not generalize them to Trust breadcrumbs or arbitrary destinations.

Do not modify, refactor, relocate, retune, or indirectly change Necrohazard behavior or its supporting helpers unless Chris explicitly requests a Necrohazard change. A general request to work on Yuweyawata, boss two, Soulweave, diagnostics, movement priority, or nearby shared code does not grant that authorization. If another requested change would overlap Necrohazard-owned code or shared behavior in a way that could alter it, stop and ask for explicit direction.

This freeze exists because earlier live captures validated the core path selection and forced-movement behavior, while the 2026-08-26 failure isolated the remaining defect to the bounded route-entry handoff documented above. Unrelated boss-two changes must not regress either behavior.
