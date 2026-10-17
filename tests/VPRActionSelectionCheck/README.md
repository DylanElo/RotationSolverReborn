# VPR action selection boundary

Run `dotnet run --project tests/VPRActionSelectionCheck -c Release` from the repository root.

The runner compiles the current production `VPR_Reborn.GeneralGCD` in full, the Sync GCD entry, action gates, candidate hooks, cached `Decision`, snapshot evaluator, target lifetime/dying checks, planned burst payload length, party-buff scan and burst/overlap-clock tracker, and the complete scheduler/policy types using the SDK's existing Roslyn assemblies. It invokes the production selector and asserts its returned action and resolved target. The boundary replaces game services, dungeon/diagnostic callbacks, gauge/status snapshots and native `CanUse` with deterministic eligibility and target resolution. It does not simulate the full native Dalamud action pipeline, effects, animation locks or DPS.

The cases cover free-token downtime spending without changing no-token policy, ordinary buff holds, native veto/disabled/no-target fallthrough, upkeep/combo/followup safeguards, ranged uptime, target-specific debuffs with global cadence preserved, both directions of native target divergence, self-centred attacks and their affected hostiles, same-time target switching in the actual decision cache, short-lived/dying native candidates despite a healthy default and the reverse, single versus double Reawaken payload overlap, scoped exception cleanup, overlap-clock release across timestamps, Reawaken priority, and the actual EmergencyAbility potion path for the selected native Reawaken candidate. Native candidate resolution is preserved; the scheduler does not force its default enemy onto an action.

`--source-directory <directory>` compiles alternate production sources from a folder containing `VPR_Reborn.cs` and `VPR_SyncPlus.cs`, allowing the same cases to run against a clean Git revision without changing the working tree. Both source byte hashes are printed.

`--before` reads the clean source snapshots under `artifacts/elite-intent-20261017/before/` and is expected to fail the regression cases. These snapshots are review artifacts, not runtime inputs or dependencies of the normal test command. No plugin DLL is built or deployed.

For self-centred multi-enemy attacks, the scheduler retains the default hostile when it is actually affected; otherwise it uses the first affected hostile. A dying affected default therefore conservatively withholds burst even if another affected enemy is healthy. These cases validate the generic native target contract, not an asserted Excel targeting mode or an optimal weighted AoE damage policy.
