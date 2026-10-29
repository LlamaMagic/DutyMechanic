# Sohm Al encounter verification

Domestic accepted this implementation on October 29, 2026 after final-behavior clears
as Paladin, White Mage, Viper and Black Mage. All four runs had zero player deaths and
zero boss vulnerability gains, autonomous navigation/completion/exit, restored SideStep,
the original SlideMover and no remaining owned avoid registrations after exit.

## Ownership and mechanic decisions

The standalone profile retains its three authored objectives, dragon gates, shortcut
and entry exceptions. Canonical profile revision `85e1915` corrects the Myath grind
guard, uses Duty Support by default and enables the declared plugins. Boss mechanics
remain in DutyMechanic; SideStep owns the intervening trash and the routine owns combat
and cooldown planning.

The handler uses scalar geometry for each cast/helper, actual targets for Mad Dash
stack/spread, and a marker-to-fixed-impact handoff for peripheral meteor placement.
Tioman's eight Chaos helpers remain separate hazards, and boss ownership persists
through the invincible wing phase. Exact registration handles can be reacquired after
external clears without duplicating registrations.

One adapter on the existing mover channel arbitrates stack requests and observes native
escape commands. Cleanup releases only an outstanding command still owned by that
adapter; replacement movers and successor commands retain control. This accommodates
the observed RB 1.0.941 escape-path cleanup leaving forward input held. It does not
choose dodge destinations or use NPC locations as incidental safe-position proxies.

Object `2005287` belonged to trash Fireball `3928` in the original handler. Its later
unscoped boss avoid was removed on that evidence; trash ownership remains with SideStep.
Developer-only signal logging retains object visibility evidence without controlling it.

## Verification and limits

The final handler passed 93 detached real-source geometry, overlap, target and lifecycle
checks, full integration compilation, and these normal level-synced Duty Support runs:

| Role | Normal completion (UTC, October 29) | Boss vulnerability gains | Result |
| --- | --- | --- | --- |
| Paladin | 00:18:03 | 0 | Pass |
| White Mage | 00:39:22 | 0 | Pass |
| Viper | 01:00:23 | 0 | Pass |
| Black Mage | 01:25:15 | 0 | Pass |

Live evidence includes self Sweet Scent draw-in and Devour egress, self/NPC stacks,
spread isolation, self meteor placement/egress, eight-spoke Chaos, wing-floor overlaps,
routine execution during semantic positioning, and movement/ownership handoffs.
Trash Acid Shower vulnerability observations are retained separately under the agreed
boss-only allowance. No manual rescue, unresolved navigation stall or post-escape wall
run occurred. Brief sub-half-yalm Viper movement at a live Devour boundary resolved
autonomously and did not persist after the hazard.

**Explicit acceptance limitation:** Leafstorm `3798` did not occur in the four final
runs. Its room-wide mitigation classification is supported by the installed client's
50-yalm effect, a historical cast and encounter reference; its resolved live effect
was not verified. Domestic explicitly accepted this single documented live-test gap.
Do not describe detached checks or the historical canceled cast as live effect coverage.

Meteor damage has proximity falloff; the conservative 15.5-yalm avoid is not a proven
binary damage boundary. One White Mage escape overlapped an ordinary spell cast and
was still inside that conservative floor near cast end before completing egress.
It produced no vulnerability gain or death. Delayed damage announcements do not prove
the server's snapshot time; preserve this timing limitation rather than claiming a
universally verified snapshot margin.

The outgoing diagnostic switch is disabled. The gated recorder remains available for
future evidence collection. These dungeon results do not authorize campaign rating
changes before the full Duty Leveling campaign passes.
