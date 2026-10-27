# Duty Mechanic 7.5.4282015.4

Accepted for release October 26, 2026. The cleaned source compiled and loaded as
Duty Mechanic 7.5.4282015.4 in the main Magitech installation attached to Kember.
DW confirmed that successful build loading is sufficient for this release;
mechanic issues will be addressed as reported. Two installation updates remain
blocked by filesystem permissions.

## Changes

- Reconciled the main tree with local Toto-Rak, Aulus, Castrum Abania and Lahabrea work.
- Preserved the newer main-tree encounter fixes rather than replacing them with older installation copies.
- Disabled the development collector and removed the latest controllers' actor watches, map dumps, recurring state reports and diagnostic-only fields.
- Kept bounded warnings for missing geometry or safe destinations, plus useful action and recovery messages.
- Expanded control flow, shortened investigation comments and consolidated plugin lifecycle cleanup.
- Added Lahabrea's territory registration and encounter-local disable cleanup without changing shared dungeon disposal behavior.
- Preserved Yuweyawata, AbstractDungeon and the shared movement, avoidance and combat helpers byte for byte.

## Verification

| Check | Result |
| --- | --- |
| Release build with pinned official reference assemblies | Passed; zero errors and twelve existing warnings |
| Diagnostic release invariant and bounded history checks | Passed |
| Protected source hashes | Match the pre-cleanup snapshot |
| Formatting rewrite | Non-brace C# tokens checked before and after expansion |
| Lahabrea scalar regressions | Twelve checks passed: floor bounds, safe landings, persistent hazards, approach segments, damage ordering and unknown actions |
| Deployment fixture | Passed: dry run, backups, obsolete source removal, hashes and .NET 8 exclusion |
| Host assembly compatibility compile | Eight hosts passed using their own cached Quest Behaviors assembly |
| Asher host | Same RebornBuddy DLL hash as Kember; its cached Quest Behaviors DLL is empty, so independent host compilation was unavailable |
| Fresh source startup | Passed in RebornBuddy process 51804; Duty Mechanic 7.5.4282015.4 enabled without compiler errors |
| Duty dispatch and lifecycle | Halatali 7.1 entered through Duty Support at level 23 sync; release Halatali7_1 handler active; bot stop released the plugin hook |

The existing warnings include value-type null checks, unused receive-message fields
and obsolete navigator overloads. Shared movement warnings were left unchanged to
honor the protected encounter constraints.

## Deployment

The main installation and six other .NET 10 installations now have the same release
source, verified by SHA-256: Crucible-Kember, Magitech, Magitech Copy, Asher, Clean,
CN and Net10. The main Magitech host has now loaded the release source. Restart
the other updated hosts outside a duty to load it. Asher also needs startup to
rebuild its empty Quest Behaviors cache.

Crucible-Arelia and Crucible-Darling remain on their previous installation copies.
The attempted update stopped before copying release files because removing obsolete
Labyrinth source was denied. Their seven obsolete raid source files must be removed
as part of the update; those controllers now belong to Panda Farmer. Their useful
Lahabrea files have already been incorporated into the release source.

Reborn TC and Reborn Clean Copy use .NET 8 and were left unchanged. Update those
hosts before installing this .NET 10 release.

With RebornBuddy stopped and normal filesystem access, run from the main repository:

```powershell
./tools/Sync-Installations.ps1 -InstallName 'Reborn - Crucible-Arelia','Reborn - Crucible-Darling'
```

The sync script backs up overwritten and obsolete files outside the plugin tree,
preserves local git metadata and settings, and verifies every copied file by hash.
Use `-WhatIf` to preview it. No host binaries or Panda Farmer files are changed.

## Mechanic coverage

The earlier audit records live clears for Sastasha, Tam-Tara, Copperbell and Bowl
of Embers. That evidence does not cover every role, wipe path or mechanic branch.
The later encounter additions retain their previous validation limits. In
particular, the ARR audit still lists Halatali, Haukke and Brayflox follow-up checks,
and the exact outer edge of Ifrit's revised frontal avoid remains unisolated.

The optional Halatali run was ended after startup and dispatch checks when DW
confirmed that full mechanic verification is outside this release's acceptance
criteria. The run did not reach a boss and is not a mechanic-validation clear.
Production diagnostics remained disabled; the temporary read-only observer was
kept outside the installed source and removed after the test.

The remaining mechanic checks are follow-up work, not release blockers under the
accepted criteria. No commit, push or public publication has been performed.
