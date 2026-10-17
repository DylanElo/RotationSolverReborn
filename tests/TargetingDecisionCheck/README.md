# Targeting decision checks

`dotnet run --project tests/TargetingDecisionCheck -c Release`

`dotnet run --project tests/TargetingDecisionCheck -c Release -- --baseline-ref a22cd352`

Compiles the production `ObjectHelper.IsTopPriorityHostile` overloads, FRU crystal fields/method, the complete native `ObjectHelper.IsAttackable` and `TargetHelper.ValidityCheck`, and the complete native `ActionTargetInfo.FindHostile` and `FindHostileRaw` local functions using the SDK's existing Roslyn assemblies. The baseline option reads both production files directly from Git. Source fingerprints are printed on each invocation.

The checks assert the selected object, including the native stop-marker filtering, high-priority reduction and configured targeting sort. External candidate construction (range, LOS and native `CanTarget`), object table, game state and legacy helper facts are explicit fakes in `Boundary.cs.txt`. An omitted candidate represents one rejected upstream; this suite does not independently verify native memory reads or implement those rejection rules. It tests that a rejected NW object still present in the object table cannot suppress reachable SW/remaining crystals. The existing MCH Wildfire priority can compete with crystal priority and is intentionally retained.

HEAD a22cd352 has no FRU crystal ranking: 36 actual decisions fail. The prior uncommitted implementation passes 11 and fails two (omitted NW and stop-marked NW). The corrected candidate-aware caller and all-crystal attackability inclusion pass all 51 cases, including restrictive targetless membership, immune/blacklist/stop/untargetable/one-HP guards, BRD/MCH/VPR default crystal choices and every native targeting mode without changing ordinary encounter selection. The original one-argument priority signature remains available.
