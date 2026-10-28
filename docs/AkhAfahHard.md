# Akh Afah Amphitheatre (Hard)

This handler supports Shiva's level-50 Hard trial, territory 377 and duty-finder
condition 79. These notes preserve the mechanic evidence and verification limits;
a fast clear must not be mistaken for coverage of a skipped phase.

The standalone profile owns matched-party entry, arena arrival, Ice Soldier priority,
completion, loot and exit. Non-tanks wait for the party to pull. OrderBot and the
combat routine retain target selection and job actions.

## Mechanic ownership

- Hailstorm uses the actual targets of action 2456. A marked player separates from
  other living players; an unmarked player avoids the marked players. The helper's
  location and the controlled player's own marker are not escape destinations.
- Icicle Impact, action 2462, uses ten-yalm circles with a half-yalm margin. When all
  nine circles briefly cover the floor, native avoidance receives the earliest
  cohort, then the remaining wave and late center once the first opening exists.
- Glacier Bash, action 2465, has no usable omen. Repeated diagnostic frontal hits
  confirmed the missing response. Nearby players move directly behind the caster
  through the existing input gate, provided the short path and destination are safe.
  The rear hold lasts through the observed delayed damage application. Distant
  players remain in place; the implementation does not assert an unmeasured cone angle.
- Permafrost, action 2457, suppresses travel rather than registering the entire floor
  as damage. Its hold bridges the observed delay before Thin Ice status 604 arrives,
  then follows the status until removal. Healing and other stationary actions remain
  available. Each routine capability is leased separately, as required by RB.
- Raidwides retain routine healing and mitigation. Hard-mode Icebrand is a tank
  cleave, not a shared stack; tank facing remains ordinary combat positioning.

Native avoidance owns hazard paths. The encounter's one input gate arbitrates
Thin Ice, native escape, Bash positioning and ordinary travel. It also releases
outstanding native escape input on affected RB builds. Native hazards and Thin Ice
preempt Bash traversal; arrival and mechanic release stop only owned input.
Stop, death, loading, exit and plugin disable release borrowed state. A detached
gate cannot restore or stop a successor mover. SideStep is suppressed only during
this encounter and restored afterward to prevent duplicate or inverted geometry.

## Acceptance and remaining coverage

The common matched-party mechanics cleared on Paladin and White Mage without
recorded deaths, vulnerability gains or failed escape searches. The final candidate
has 36 passing deterministic policy/ownership checks and a successful Release build.

Private undersized captures informed the Bash and Thin Ice fixes. They are diagnostic
evidence only, not acceptance of behavior with live players. On October 28, 2026,
Domestic accepted completing this profile when matched groups skip later mechanics,
provided those gaps stay documented. Do not slow a live group or resume private
damage-limited captures to manufacture coverage.

The final candidate completed a synchronized eight-player White Mage run at
23:18:22 UTC on October 28, leaving the duty at 23:18:37 and stopping outside at
23:18:42. It recorded zero deaths, vulnerability gains, failed escape searches or
encounter/observer exceptions, with 55 routine action starts and 44 successful casts.
Installed encounter source matched the tested worktree.

The outstanding live-party cases are close-range Bash,
Thin Ice and its overlaps, deliberate main-tank cleave positioning, and frozen-player
rescue. Ice-boulder targeting has not been added: its Hard-mode actor identity and a
natural rescue remain uncaptured. Profile acceptance does not imply full mechanic coverage.

## Release 7.5.4282015.17

This version adds Shiva's handler, territory registration and lifecycle cleanup, with
the matching standalone profile in DWsWebserver. Cleanup clarifies ownership and timing
comments and makes formatting consistent without retuning the accepted candidate.
Raw capture ledgers and diagnostic harnesses remain outside the release.

Release checks compare normalized C# syntax and expanded XML against the accepted
candidate, allowing only comments, whitespace and optional statement braces. All three
encounter files and the profile preserve their tested behavior; all 36 policy/movement
checks pass. The Release build succeeds with no errors and 13 existing warnings, and
the shared diagnostic facility passes its invariant check with diagnostics disabled.
Every released file was reviewed for the rationale needed to maintain its changes.
The cleaned source was not deployed for another gameplay run.
