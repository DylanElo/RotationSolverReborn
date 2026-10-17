# CT, weave integration and native replay validation

This snapshot completes the BRD CT recovery and shared weave-budget integration on top of the MCH changes in `ae7e669e`. It deliberately leaves unrelated BRD proc/DoT, Viper and target-selection work outside this change. Build outputs below are separate from the installed development plugin.

Run from the repository root with .NET 10 and the existing Dalamud SDK available:

```powershell
$build = Join-Path $PWD 'artifacts/complete-snapshot-build'
dotnet build RotationSolver/RotationSolver.csproj -c Release "-p:OutputPath=$build/" -p:GenerateDocumentationFile=false
dotnet build tests/FightSim -c Release
$plugin = Join-Path $build 'RotationSolver.dll'
$sim = 'tests/FightSim/bin/Release/net10.0/FightSim.dll'

dotnet run --project tests/WeaveBudgetCheck -c Release -- $plugin
dotnet run --project tests/PlannedMchUtilityWeaveCheck -c Release -- $plugin
dotnet run --project tests/MCHSyncPolicyCheck -c Release -- $plugin
dotnet run --project tests/MCHSyncPlusPolicyCheck -c Release -- $plugin
dotnet run --project tests/MCHPlanFallbackCheck -c Release
dotnet run --project tests/RotationConfigLifecycleCheck -c Release
dotnet $sim --check-mch-queen
dotnet $sim --check-mch-preparation
dotnet $sim --check-mch-queen-model
dotnet $sim --check-downtime
dotnet $sim tests/FightSim/timelines/fru-jD6z47YVBXrCbK9F-5.json --check-brd-model
dotnet $sim tests/FightSim/timelines/fru-jD6z47YVBXrCbK9F-5.json --check-ct-recovery

foreach ($report in @('fru-jD6z47YVBXrCbK9F-5', 'fru-XJpcY8aNzDnKkhL6-7', 'fru-964FmTBDCyp21Qw3-2')) {
    dotnet $sim "tests/FightSim/timelines/$report.json" --seeds 20 --check-ct-codas --seed-report
}
dotnet $sim tests/FightSim/timelines/fru-964FmTBDCyp21Qw3-2.json --seeds 20 --check-ct-codas --replay-utilities --seed-report

foreach ($fight in @(7, 8, 10)) {
    dotnet $sim "tests/FightSim/timelines/native-opportunity/fru-mch-AWXjPmH98Vqhzya4-$fight.json" --seeds 20 --seed-report
}
foreach ($fight in @(27, 5)) {
    dotnet $sim "tests/FightSim/timelines/fru-mch-jJXkAtKaFQPyH4Wn-$fight.json" --seeds 20 --seed-report
}
```

Each command must exit successfully; stop on any failure. PowerShell does not automatically stop after an external command returns a nonzero exit code. A batch runner should check `$LASTEXITCODE` after each command.

FightSim loads the local saved RSR configuration when present. Its output identifies that configuration; record it when comparing results. MCH native replay totals from the preceding validated configuration are 272.8380867, 282.3729836 and 277.0817884 potency/s for fights 7, 8 and 10. They are configuration-specific reference results, not fixed assertions for arbitrary user settings.

The [native fixture provenance](FightSim/timelines/native-opportunity/README.md) documents observed creation, targetability, HP and cast-end corrections. Historical inputs remain available alongside the corrected versions.

The CT checks exercise selected actions and modeled outcomes: confirmed Finale, Coda rebuilding, the P5 Minuet return, subsequent three-Coda Finales and song coverage. The utility replay additionally audits the emitted cast stream against the two-weave limit and recorded mitigation windows. Its window starts are sensitivity inputs anchored to recorded casts, not proof of original BMR state-machine activation times.

WeaveBudgetCheck executes the production budget's request/effect/reservation paths and checks the built plugin's integration call chains. It does not execute Dalamud hooks against a live client. Manual and other-plugin actions are observed; RSR can limit only its own action issuer.

No command above installs or reloads a plugin. Build/test results do not establish native action issuance, pet travel, live phase acceleration or successful plugin loading. All retained MCH replay inputs are wipes.
