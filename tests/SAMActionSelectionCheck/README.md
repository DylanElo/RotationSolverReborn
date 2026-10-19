# SAM action selection regression checks

Run `dotnet run --project tests/SAMActionSelectionCheck -c Release` from the repository root.

The harness extracts the current production SAM action selection, DoT gate, burst decision,
party-buff scan and candidate-context members using the SDK Roslyn assemblies. It also
compiles the unchanged production SAMSyncCycle and scheduler/policy types. No package
or runtime dependency is added.

Twenty scenarios cover friendly hard targets, resolved enemy DoT refresh and clipping,
changed final candidates, candidate death/TTK, native rejection/status-provide flags,
other-boss anchor observation versus current overlap, same-frame target switches,
Ogi candidate burst qualification, self-centred affected targets and context restoration.

Before source changes the first twelve scenarios returned 3/12 passed, with failures
for friendly-target clipping, alternate-candidate refreshes, final-candidate changes,
candidate death/lifetime leakage, other-boss overlap and stale target caches.
After the source changes all twenty pass.

The external boundary simulates native CanUse target resolution and rejection; it does
not execute Dalamud or game action validation. These checks establish source behavior,
not live casts or game activation. No deployment is performed.
