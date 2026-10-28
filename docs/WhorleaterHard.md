# The Whorleater (Hard)

Territory 281, duty-finder entry 72. Use the standalone Whorleater profile from
DWsWebserver. This handler does not apply to Extreme.

The profile owns entry, role-specific targets, adds, completion and exit.
DutyMechanic supplies deck hazards, converter interaction, center staging and
head/tail assignments for tanks. OrderBot and the selected combat routine retain
combat, rotation and healing.

## Movement and targeting

Native avoidance takes priority over converter interaction and center staging.
The encounter wraps the existing navigator and mover because routine capability
flags alone do not prevent OrderBot from pursuing a submerged boss. A stale path
or callback must not stop the next movement request. Exit and stop restore only
the services still owned by this encounter.

During the shield or when no boss is attackable, gather within three yalms of
center. Resume staging only beyond five yalms, and defer it if a known hazard
covers center. Once stationary, allow the routine to continue healing.

The closest living party member handles the converter. Five seconds without
progress selects another member; temporary failures remain retryable.

Tanks take opposite pieces based on the other tank's aggro or selected boss.
Two seconds of consistent evidence is required before switching. Assignments
survive submerges, while death, exit and stop clear them. For Magitek Paladin and
Warrior, off-target ranged pulls are temporarily disabled during the split and
restored afterward without saving preferences. Other routines retain their own
action selection.

## Verification

On October 28, 2026, synchronized runs cleared with Paladin, Monk and White Mage.
Tank runs exercised both head and tail assignments. The healer held center and
continued casting during staging. The final healer capture contained no player
death, vulnerability gain, jump-probe or wall-follow records.

The publication cleanup expands compact control flow and shortens comments.
Darkness pose logging now uses the existing developer diagnostic switch; the
shared collector and its lifecycle remain intact. Movement priorities, timings,
target weights and profile actions are unchanged. Source validation and replay
checks cover the cleanup; the final formatted revision has not had another live run.

Remaining limits:

- Earlier tank and melee runs needed brief navigation recovery after slams.
- Dive helper handling still needs a live capture.
- Darkness cone geometry is unverified. It is captured for investigation rather
  than registered as guessed avoidance.

These limits do not invalidate the observed clears, but they must remain visible
when extending or retuning this encounter.
