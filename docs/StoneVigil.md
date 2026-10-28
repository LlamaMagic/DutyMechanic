# Stone Vigil maintenance

Stone Vigil supplies boss hazards to RB's native avoidance path and suspends SideStep
only while a boss encounter owns those hazards. The combat routine retains ordinary
combat, mitigation actions and cooldown planning. The standalone dungeon profile owns
the authored route, three director objectives, Dragon Aevis targeting and Strongroom Gate.

## Constraints established by live verification

- Tornadoes use damage actors (NPC 9910, base 14536), excluding choreography helpers.
  Register scalar positions to avoid RB's attraction toward a collection's first actor.
  Block remaining westward travel with 3.5-yalm clearance, including pauses between steps;
  release passed eastern floor so overlapping wave pairs leave a cross-lane escape.
- Koshchei and Isgebind use square combat boundaries with a half-yalm wall inset.
  Cauterize uses each caster's 48-by-20 rectangle plus clearance, including simultaneous
  helpers. Party NPC positions are not used as safe-position proxies.
- RB's captured SlideMover can retain forward input after native escape ends. The scoped
  mover adapter forwards commands unchanged and releases only its own outstanding escape
  input. A new ordinary command supersedes cleanup; detached callbacks and foreign mover
  replacements cannot stop a successor. Restore the original mover on encounter exit,
  loading, death, stop and plugin disable. Do not install this workaround globally.
- The profile retains Domestic's approved 20-yalm Dragon Aevis acquisition limit and
  OpenDoors interaction hook. The gate wait precedes the existing room crossing. Keep
  route and objective changes separate from mechanic changes.

## Acceptance and publication

On October 28, 2026, Domestic accepted tank and healer and then accepted Stone Vigil as
complete. Viper correction5 and Black Mage correction6 passed the campaign review. The
final Black Mage run had zero player deaths, boss vulnerability gains, tornado Stun,
Frostbite and anti-stuck events; six Cauterizes resolved safely. SideStep and the original
mover were restored at normal exit. Two recorded deaths belonged to the same party NPC,
not the player. Earlier failed attempts remain failed diagnostic evidence.

The isolated earlier Black Mage Cauterize stall did not recur. A clean subsequent run
does not establish that stall's root cause; retain the default-off command diagnostics
for recurrence. The final cleanup changes comments and whitespace only in the tested
handler, mover adapter and profile. Publish the required Stone Vigil lifecycle calls
separately from other in-progress dungeon work, with shared diagnostics disabled in the
published plugin. Campaign ratings remain unchanged until the full campaign completes.
