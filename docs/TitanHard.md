# The Navel (Hard)

The territory 293 handler keeps Geocrush's surviving floor separate from its falloff
damage. Each observed landing shrinks the boundary to 23, 20 and finally 15 yalms,
with a half-yalm inset. The player stages near that boundary before impact. Personal
death preserves the collapse count; a fresh full-health boss resets it. Mid-fight
attachment conservatively uses the smallest floor until a new pull.

Landslide uses an explicit knockback lane. Bomb avoidance follows the earliest cast
wave instead of forbidding every bomb simultaneously, leaving later rows available
for escape. Weight of the Land retains SideStep's ground-targeted handling: its
helper actor is parked away from the actual circle. Instant frontals exempt only
Titan's current aggro holder. Native avoidance owns destinations; an encounter-local
adapter releases its completed input without stopping successor movement.

The standalone profile defaults to synchronized matchmaking, prioritizes Granite
Gaol over the heart and boss, and leaves bombs out of combat targeting. The original
center hotspot remains the fallback. Loading, death, completion, loot and exit use
the established shared profile tags.

## Verification

October 28 local testing used Arelia in synchronized matched parties on Global
2026.10.16 / RB 1.0.941. The White Mage candidate cleared without a recorded death
or exception. It covered all three Geocrush stages, targeted Weight circles,
Landslide, circular and three-row bombs, bomb/Landslide overlap, heart targeting,
gaol rescue priority and recovery after Arelia was jailed and freed by the party.
The complete three-row bomb sequence ended at full health.

The same candidate also cleared on Paladin without a recorded death or exception,
covering all floor stages, gaol priority and bomb/Landslide overlap again. Arelia
briefly held aggro and otherwise acted as off-tank; this is not a sustained main-tank
test. Both runs exited normally and released the encounter's movement adapter.

Twelve production-linked movement checks cover active escape, interruption,
overlapping hazards and successor ownership. Wipe/re-entry, mid-fight attachment,
the center/cardinal/diagonal bomb layout and a dedicated DPS run remain unverified.
No undersized or deliberately slowed parties were used to force those cases.
