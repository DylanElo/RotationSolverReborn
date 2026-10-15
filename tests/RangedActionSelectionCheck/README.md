# Ranged action selection regression check

Run from the repository root with the installed .NET 10 SDK:

```powershell
dotnet run --project tests/RangedActionSelectionCheck
dotnet run --project tests/RangedActionSelectionCheck -- --baseline-ref f681d72
dotnet run --project tests/RangedActionSelectionCheck -- --baseline-ref 6a3a90eb
```

The first command must exit 0. The historical commands are negative controls and must exit 1 with named behavioral failures. They read historical source with `git show`; they do not change the checkout. No NuGet packages are added: compilation uses Roslyn shipped with the SDK.

At each run, Roslyn extracts and compiles the complete, unchanged production method bodies for BRD `GeneralGCD`, `CanUseApexArrow`, `TryApplyDoT`, snapshot tracking and refresh valuation, MCH `GeneralGCD`, `AttackAbility`, the Reassemble reservation, and both MCH Sync+ selection overrides. The complete production MCH burst and Sync+ policy classes are included. Each method's SHA256 prefix is printed. The tests assert selected action IDs and exercise the multi-step Air Anchor → reserve Reassemble → Chain Saw → consume Reassemble sequence. There are no copied selector branches or static IL ordering assertions.

Coverage includes the three original counterexamples, normal and early Iron Jaws competition with Apex, target death and unknown TTK, disabled/boss-only/independently resolved DoT targets, Reassemble cap and tool/target availability, FRU padding, standalone CT Encore and Full Metal, Barrage/proc priority, and the Wildfire → Full Metal handoff.

## Boundary and limitations

This is a **production selector integration check with a simulated action/game boundary**. `Boundary.cs.txt` supplies action objects, status data, cooldowns, target results, buff weights, gauge values and scheduler flags. Native `CanUse`, target geometry/selection, IPC timing, the complete CustomRotation dispatcher, Queen/Hypercharge sub-decisions and live game services are not executed. `CanUse` models enable/learn/readiness/TTK and status-provide eligibility relevant to these cases; statuses use the earliest matching present alias, defaulting to zero when absent. Synthetic buff weights and seeded per-target snapshot records allow exact numerical counterexamples while executing the real snapshot and selector methods.

Keep build/policy suite results and live game evidence separate. Passing this check proves the composed source selection behavior under its explicit inputs; it does not prove live casts, target hit registration or a measured DPS increase. Upgrade to a real runtime replay when the game/native services can be initialized deterministically.
