# Solo Variant dungeons

Each dungeon owns its handler; Rokkon uses mechanic-specific partials to keep its large
encounter sequences reviewable. Encounter actors and casts select the mechanics,
so the handler does not depend on a profile's selected route or saved progress.
Registration uses the solo Variant territory only; Criterion and Savage are excluded.

| Dungeon | Territory | Included encounters |
| --- | --- | --- |
| The Sil'dihn Subterrane | 1069 | Geryon, Silkie, Gladiator, Zeless Gah, Thorne Knight |
| Mount Rokkon | 1137 | Yozakura, Moko, Gorai, Shishio and Enenra |
| Aloalo Island | 1176 | Quaqua, Ketuduke, Lala, Statice, Loquloqui |
| The Merchant's Tale | 1315 | Genie, Pari, Darya, Rukhkh, Swordmaster and Dandan |

The profiles own traversal, interactions, target order and rewards. OrderBot and
the combat routine retain combat scheduling. Geometric hazards use RB avoidance;
direct movement is reserved for positive positioning and the documented overlaps
where live captures showed that independent avoids could not preserve impact order.

Keep action ownership, effect-delay margins and cleanup together when changing a
mechanic. Forecasts retain scalar state rather than native actor wrappers. Restore
SideStep overrides and capability leases on exit, and leave unknown sequences to
the documented fallback rather than inventing timing after a mid-sequence attach.
Detailed encounter traces use `LoggingHelpers.MechanicDiagnosticsEnabled`.

The consolidated source includes the latest deployed Sil'dihn fixes from September
30 and the corresponding Aloalo, Rokkon and Merchant's Tale handlers. Consolidation
preserves mechanic values and statement order; its runtime change is gating local
trace output. Coverage of an encounter does not mean every mechanic permutation
has a verified live clear. The source retains the outstanding capture limitations.

Run `Tests/Variant/Run-Replays.ps1` with .NET 10 for the landing, shelter, paired-gaze,
sewage-order and Thorne geometry regressions. The runner extracts the production
helpers and compiles outside the plugin tree; it does not need a running game.

## October 2 integration

The Rokkon and Merchant handlers combine the deployed route-test implementations.
Rokkon partials keep each mechanic's forecast, ownership and cleanup together; all
parts must ship with the main handler. Formatting and comment cleanup preserve the
captured C# token stream, including geometry, timing and fallback decisions.
The shared actor diagnostic explicitly includes the local player because object
enumeration can omit self-targeted markers. Diagnostics remain gated by the existing
compile-time switch; no persisted setting or additional UI is introduced.

Merchant's Tale completed its first full thirteen-route rotation on October 2.
Dandan's final pull had no deaths and retained routine actions during movement holds,
but still recorded a Maw vulnerability and a survived bubble/Devour bind. Those two
sequences remain refinement targets; a clear does not establish clean avoidance.
Rokkon's Moko and right-route overlaps also retain their recorded capture limitations.
No unvalidated staging candidate is included in this integration.
Rokkon's detached capture regressions are under `Tests/Variant/Rokkon`. Run each
`Test-Rokkon*.ps1` in a fresh PowerShell 7 process: Add-Type fixtures deliberately
use isolated type names. Together they check cloud sequencing, timed travel,
knockback landings, roots, towers, smoke, swipes and moving hazards against the
production partials. These tests do not claim live CN or TC encounter validation.

## Boss ownership and retained failure context

The follow-up October 2 integration moves the remaining normal boss telegraphs for
Rokkon and Merchant's Tale into their dungeon handlers. SideStep resumes for trash
and traversal after the handler releases its suspension. Merchant's version-specific
handoff removes only SideStep's captured registered avoids; unfamiliar layouts retain
the fallback and report the missing contract. Neither handler clears global avoidance.

Rokkon has live boss acquisition/restoration evidence, including a clean right-route
Yozakura fight. A later Shishio cloud hit remains unresolved. Merchant's new ownership
handoff has begun live validation; earlier thirteen-route clears do not validate all
newly transferred casts. Keep the existing Dandan limitations above until fresh
captures establish their outcomes.

Diagnostic cast history now lasts through estimated cast completion plus thirty
seconds, bounded to 128 entries. Failure reports also retain fifteen seconds of
movement context and three seconds afterward. These scalar snapshots are passive;
last movement commands may be stale and nearby casts do not prove damage attribution.
The diagnostic switch remains off in published source.

Run `Tests/Variant/Test-MerchantOwnership.ps1` and the Rokkon ownership replay alongside
the existing geometry checks. `Tests/Diagnostics/DiagnosticHistory.csproj` tests real
history expiry, capacity and reset behavior. `Tests/Diagnostics/Verify-Invariants.ps1`
checks lifecycle wiring and the retained-history implementation; its checks replace
the older validator's assumptions about a FIFO queue and a fixed cast-start window.

## Retained telegraphs and escape handoffs

The next integration preserves Rokkon's last valid Levinblossom, Icebloom, Iron
Rain and Clearout origins when native omen data disappears. Measured post-cast
hits justify longer Levinblossom, Iron Rain and Fireblossom retention; Fireblossom
also leases gap closers so Slither cannot return into a resolving circle.
Gorai's timed orb transfer avoids the disconnected paths produced by treating
future explosions as current obstacles. It requires four known orbs, one small
orb, an unobstructed platform and enough travel time. Unknown overlaps and late
arrivals retain native avoidance. Lifecycle cleanup releases only its own lease.

Merchant prepares Firecrackers' safe side during the preceding cannon waves,
stops residual forward input when Sparks avoidance hands back movement, and
interrupts the observed Red Mage hardcasts during specific active escapes.
Sandplume interrupts Verfire only; the other captured cases include Verstone.
The published cleanup preserves the source tokens, geometry and timing, while
shortening incident narratives and expanding dense control flow for maintenance.

Additional replays: `Tests/Variant/Test-GenieOverlap.ps1`,
`Tests/Variant/Test-SwordmasterCastEscape.ps1`, and the Rokkon Gorai-orb and
Levin-retention scripts. These exercise production selectors and captured geometry.
Lash's Verstone interruption has live evidence; Gorai orb movement, Sand Pearl's
Verstone case and the latest Sandplume change still require live acceptance.
No claim of CN/TC encounter validation is made by these detached checks.
