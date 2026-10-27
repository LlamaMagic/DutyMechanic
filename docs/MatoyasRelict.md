# Matoya's Relict

This handler keeps encounter mechanics out of the dungeon route. The standalone
profile owns travel, transport interactions, required trash and duty completion.

`MatoyasRelict.cs` owns dispatch, Mudman, the shared positioning lease and cleanup.
`MatoyasRelict.Bosses.cs` contains shared hazard tracking, Nixie and Mother Porxie.
The plugin's pulse and reset callbacks release encounter resources even when
death, loading or bot stop prevents the normal encounter hook from running.

## Movement contract

Native avoidance handles ordinary damaging shapes. The existing positioning owner
handles shared hits, charge baiting, required geyser entry and knockback staging.
During Barbeque/To a Crisp it temporarily owns continuous forward/lateral input:
stopping at a destination or dodging backward can allow lethal suction. It yields
until an already-running native escape releases input, and releases its movement
and facing leases when the phase or encounter ends. RB's capability update accepts
one flag per call; cleanup accepts a mask for this owner's flags.

SideStep is suspended only for the active boss encounter and restored only if this
handler disabled it. Cleanup removes this handler's own avoids, never global ones.
Hazard objects retain identity while their observations refresh, preventing a
fresh escape path from being created every frame.

## Cleanup validation

The October 26 cleanup expands branches and loops, applies repository formatting,
and clarifies ownership and captured timing assumptions. Action IDs, hazard geometry,
timers, target selection and movement expressions are unchanged. The only gameplay
correction includes the observed Y=60 apex in the existing Porxie flight guard:
the strict upper bound reset the pending instant-puff state during flight. Every
other non-brace executable token and the import sets match the pre-cleanup source;
added braces were produced by the C# IDE0011 formatter.

- Full canonical-source compile: zero errors, thirteen existing warnings.
- Extracted production geometry/identity checks: 120 passed, including the captured
  apex and rejection immediately above the observed flight boundary.
- Diagnostic invariant: passed; the release default remains disabled and the
  permanent diagnostic collector/lifecycle remain present.

These checks establish source compatibility, not clean gameplay. The independent
live replay uses the preceding installation; the cleaned source and apex correction
have not been deployed. That run demonstrated sustained Barbeque movement and
normal release without the former capability exception, but still took a debris
hit and later Medium Rear damage after the apex reset. Airborne Huff and Puff
retention, Nixie pipe timing and clean Barbeque overlaps remain pending live
acceptance. Preserve the evidence rather than inferring success from compilation
or a recovered clear.

The replay completed and exited with zero logged deaths or exceptions. It passed
both earth pads, both water-spout handoffs and the Molten Phoebad approach without
manual intervention. The debris and Medium Rear hits above prevent a clean-fight
claim. Vulnerability messages at subzone changes repeated the existing status;
they are not additional damage events. The profile on disk changed during the
run, so this acceptance applies to the loaded 10-yalm required-kill revision, not
the later 20-yalm revision.

## Intended change scope

The Matoya change comprises the two encounter source files, its two plugin
lifecycle calls, and this document. Unrelated local documentation and deployment
tools are outside this change. No commit, push or publication is implied by the
cleanup checks.

## Commit scope confirmed

Domestic subsequently requested all pending changes in this repository be committed,
including the existing DutyProgressionAudit and ReleaseReadiness documents. The
changelog now records Matoya's changes under Unreleased. Source hashes still match
the cleanup revision described above, with 120 checks and successful compilation;
this commit does not upgrade the remaining live-verification gaps. Changed-file
rationale is documented beside behavior and in these owning records. No deployment,
version bump, push or publication is part of the commit.
