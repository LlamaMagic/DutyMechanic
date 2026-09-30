# Solo Variant dungeons

Each dungeon has one source file. Encounter actors and casts select the mechanics,
so the handler does not depend on a profile's selected route or saved progress.
Registration uses the solo Variant territory only; Criterion and Savage are excluded.

| Dungeon | Territory | Included encounters |
| --- | --- | --- |
| The Sil'dihn Subterrane | 1069 | Geryon, Silkie, Gladiator, Zeless Gah, Thorne Knight |
| Mount Rokkon | 1137 | Yozakura and Moko on the left route |
| Aloalo Island | 1176 | Quaqua, Ketuduke, Lala, Statice, Loquloqui |
| The Merchant's Tale | 1315 | Genie and Pari on the residential route |

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
