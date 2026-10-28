# ARR Duty Leveling maintenance

The October 2026 campaign gives DutyMechanic exclusive boss-hazard ownership while
SideStep remains available between fights. Each handler restores the predecessor's
actual SideStep state on completion or interruption. Plugin pulses maintain lifecycle
checks when profile interactions retain the encounter hook; plugin stop/disable resets
the same owners. Ordinary routing and combat remain with OrderBot and the combat routine.

| Dungeon | Reason for the published change |
| --- | --- |
| Sastasha | Publish confirmed Chopper/Denn geometry and prioritize Madison's dogs and Denn's guards through OrderBot's targeting provider, avoiding competing POI rewrites. Restore only the targeting provider still owned by the handler. |
| Tam-Tara Deepcroft | Handle altar/Galvanth hazards and constrain the captured altar-back traps after the orb objectives permit bridge travel. Keep the bridge approach open and remove only the handler's own travel constraints on interruption. |
| Copperbell Mines | Handle the reworked boss actions, keep Kottos escapes inside the circular room, and resolve the earlier Smash circle before activating the later Blast helper group. |
| The Thousand Maws of Toto-Rak | Correct the Coeurl identity and distinguish Silkscreen, actual pod damage and persistent poison pools from target announcements. Preserve authored add/tail priorities. |
| Haukke Manor | Handle Petrifying Eye as a bounded look-away response, including an in-flight cast and forced-turn recovery. Keep native travel priority and restore facing/input state after the gaze. |
| Brayflox's Longstop | Preserve rescue targeting, sequence poison bursts, and prevent inactive boss registrations from suppressing SideStep's trash response. Request tankbuster mitigation through the routine; retain ordinary handling of the accepted Aiatar arrival damage. |

These encounter-local changes must not become a global movement override. Stone Vigil's
separately verified input-lifetime accommodation is documented in [StoneVigil.md](StoneVigil.md).
Preserve frozen encounter behavior when extending shared infrastructure.

## Verification and publication boundary

Sastasha, Tam-Tara, Copperbell, Toto-Rak and Haukke completed the campaign's four-role
review on their final behavior. Brayflox's tank was explicitly accepted by Domestic;
its White Mage, Viper and Black Mage runs passed. Failed and superseded attempts retain
their diagnostic status. The criterion concerns player deaths and boss vulnerability
gains, with navigation, ownership and required mechanic coverage reviewed separately.

The publication reuses those tested handler sources without gameplay changes. Its
detached checks cover ownership, geometry/sequence decisions and interruption cleanup;
the exact staged source snapshot is compiled independently of unrelated local work.
Published shared diagnostics remain disabled, while the permanent default-off capture
facility is preserved. Catalog ratings wait for the entire leveling campaign, and the
remaining duties have not been certified by this release.
