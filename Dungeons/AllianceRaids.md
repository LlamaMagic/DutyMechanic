# ARR alliance raid controllers

LabyrinthRun and WorldOfDarknessRun are opt-in OrderBot tags for synced public raids.
Native objectives and subzones determine progress. The tags own positioning and
travel; the selected routine handles combat. Do not also register them with
DungeonManager, which would give two controllers ownership of movement.

A missing director never authorizes leaving an uncleared duty. Plugin pulses allow
guarded in-duty death recovery when another coroutine blocks TreeStart. They do not
queue a duty exit. Keep the MSQ consent warning: these controllers are not intended
as routine unattended farming.

Global SCH and SMN runs have been tested. Known limitations include Jaws travel and
occasional Cloud beam failures in World of Darkness. Tank/melee roles, belly/chains,
all marker and tower combinations, and other client regions are not fully verified.

The planners and route tables retain the geometry and recovery constraints required
by the controllers. Development replay programs and periodic evidence dumps are not
part of the installed plugin. Normal status, compatibility and recovery messages
remain so customer failures can still be diagnosed. Controller formatting follows
the repository's C# conventions without changing mechanic decisions.
