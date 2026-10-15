# ARR alliance raid controllers

LabyrinthRun and WorldOfDarknessRun now ship in PandaFarmerWPF's OrderBotTags folder,
with the Labyrinth engine, planners and routine lease in Runtime/AllianceRaids.
DutyMechanic must not define or pulse either controller: the relocation gives the
profile loader one owner for XML discovery, recovery and movement. Update both
plugins together and restart RB outside an active duty to retire old loaded types.

These remain opt-in OrderBot tags for synced public raids.
Native objectives and subzones determine progress. The tags own positioning and
travel; the selected routine handles combat. Do not also register them with
DungeonManager, which would give two controllers ownership of movement.

A missing director never authorizes leaving an uncleared duty. Panda Farmer pulses allow
guarded in-duty death recovery when another coroutine blocks TreeStart. They do not
queue a duty exit. Keep the MSQ consent warning: these controllers are not intended
as routine unattended farming.

Global SCH and SMN runs have been tested. Known limitations include Jaws travel and
occasional Cloud beam failures in World of Darkness. Tank/melee roles, belly/chains,
all marker and tower combinations, and other client regions are not fully verified.

Enable Panda Farmer when running either profile, including standalone OrderBot use,
so guarded death recovery remains available while another coroutine blocks the tag.
SideStep is still used for avoidance. The detached regression suite now belongs to
PandaFarmerWPF/Tools/AllianceRaidTests; it is not part of the installed plugin.

The coordinated revision restores the permanent shared diagnostic collector removed
by the previous source cleanup, with its compile-time switch false. Bot-stop,
disable and hook-clear paths discard history; no raid diagnostics are restored.
The source verifier checks the newer expiry-aware bounded history rather than the
obsolete FIFO type name. Protected Yuweyawata behavior remains unchanged.
