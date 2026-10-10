# Solo Variant dungeons

Each dungeon owns its encounter mechanics. Profiles handle traversal, interactions,
target order and rewards; OrderBot and the combat routine handle normal combat.
Only solo Variant territories are registered, excluding Criterion and Savage.

| Dungeon | Territory | Encounters |
| --- | --- | --- |
| The Sil'dihn Subterrane | 1069 | Geryon, Silkie, Gladiator, Zeless Gah, Thorne Knight |
| Mount Rokkon | 1137 | Yozakura, Moko, Gorai, Shishio and Enenra |
| Aloalo Island | 1176 | Quaqua, Ketuduke, Lala, Statice, Loquloqui |
| The Merchant's Tale | 1315 | Genie, Pari, Darya, Rukhkh, Swordmaster and Dandan |

Keep each mechanic's geometry, impact delay and cleanup together. Retain scalar
forecasts rather than native actor wrappers across frames. Release only owned
SideStep overrides and capability leases. Unknown or late sequences must retain
their existing avoidance fallback.

Rokkon uses partial classes to separate mechanics; ship them together. Movement and
gap-closer capability updates require separate API calls. Merchant's SideStep handoff
recognizes specific registered avoids; unfamiliar layouts retain the fallback.

Development traces and replay projects are excluded from this installable source
tree to avoid startup compilation errors and recurring actor dumps in customer logs.
This cleanup does not change mechanic timing or geometry. Some Dandan, Moko and
Shishio overlaps still need refinement; CN and TC encounters are not fully verified.
