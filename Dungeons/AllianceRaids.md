# ARR alliance raid profile controllers

LabyrinthRun and WorldOfDarknessRun are opt-in OrderBot tags used by their synced
public-raid profiles. Native objectives and subzones own progress. The tag owns
encounter positioning and travel; the selected routine still handles combat.
They must not also be registered as ordinary DungeonManager encounter handlers,
which would duplicate movement ownership. Missing state never authorizes leaving
an uncleared public duty. The plugin pulse permits narrowly guarded in-duty death
recovery when another coroutine blocks TreeStart; it does not queue a duty exit.

The published sources were accepted on Global SCH and SMN under the user's rule:
three completed runs with no more than two scored mechanic/travel failures each.
This is not a zero-failure guarantee. World of Darkness retains known Jaws travel
and occasional Cloud-beam defects. Tank/melee duties, belly/chains, every marker
and tower, and other client regions were not comprehensively verified. Do not
remove the MSQ consent warning or present these as routine unattended farming.

LabyrinthPlanner, LabyrinthRoutes and LabyrinthRoutineLease keep captured geometry,
alliance support rules and reversible routine ownership independently testable.
Tests/Labyrinth replays detached invariants and optional capture input; passing it
does not substitute for live navigation or role coverage. Source comments retain
the evidence, coordinate identities, margins and recovery boundaries for each rule.

This release includes only the raid controllers and their pulse hooks on top of
the existing published DutyMechanic branch. Global high-volume diagnostics remain
disabled. The authoring checkout's unrelated dungeon changes are not included.
