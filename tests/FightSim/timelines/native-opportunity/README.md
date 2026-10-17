# Native-observed MCH FRU replay fixtures

These are the three corrected `AWXjPmH98Vqhzya4` pulls used for the final
2026-10-17 Queen/Heat regression comparison. The historical timelines one directory
above remain unchanged. Use both sets when investigating a discrepancy; the older
set omits some actors and uses first-hit approximations for targetability.

Only `actors` and `enemyCasts` differ from those originals:

- Include all four Light Crystals observed in party events, including crystals
  the player never personally targeted. Existing HP and position observations
  come from FFLogs; omitted crystals use the same party-event data.
- Replace crystal attackable spans with native BossMod replay targetable starts
  and the earlier of HP zero or targetable false. A targetable dead body cannot
  extend the damage window.
- End P1 at its native HP-zero observation and include that zero in its HP curve.
- Set P2 `presentFrom` to native object creation time. Presence is independent of
  targetability; the model must not expose that body before its creation.
- Replace the P2 Absolute Zero (40224) cast duration with the observed begin-to-
  completion interval: 9.974 / 9.998 / 9.969 seconds in pulls 7 / 8 / 10.

No player actions, party buffs, plan windows, player lockouts, potency/stat inputs,
or damage observations were changed. No production rotation source is embedded.

## Provenance and limits

Source report: <https://www.fflogs.com/reports/AWXjPmH98Vqhzya4>.
Native evidence was decoded using BossMod's production `ReplayParserLog.Parse`
from `FuturesRewrittenUltimate_MCH100_Hy_Ya_2026_10_17_01_30_55.log`.
`provenance.json` records the decoded-data hash, each original and corrected fixture
hash, exact changed actor fields, crystal instance matching, and transition facts.
The raw native replay and raw event exports are not included.

Crystal instances were matched using repeated exact nonzero HP observations,
not replay damage outcomes. Native timestamps are relative to the FFLogs local
wall-clock fight start without fitting an offset. Native HP observations typically
lag matching FFLogs observations by about 0.06-0.10 seconds; each crystal's match
count and median lag are recorded. This is an observation precision limit, not a
claim that either event stream is perfectly timed.

Unannotated actors retain the legacy continued-presence assumption. These files
do not prove object removal behavior, native action availability, Queen travel or
retargeting, live action issuance, or live DPS. Focused production decision tests
cover absent/replaced objects separately. MCH seeds are deterministic; repeating
them does not create statistical confidence.

## Reproduce

Run from the repository root. Use the same saved RSR configuration for every
paired source comparison; configuration defaults are a different experiment.
These commands build only FightSim and do not install or reload a plugin.

```powershell
$fixtureRoot = 'tests/FightSim/timelines/native-opportunity'
$configPath = 'C:/Users/dylan/AppData/Roaming/XIVLauncher/pluginConfigs/RotationSolver.json'
foreach ($fight in 7, 8, 10) {
    dotnet run --project tests/FightSim -c Release -- "$fixtureRoot/fru-mch-AWXjPmH98Vqhzya4-$fight.json" --rotation MCH_Plan --seeds 20 --seed-report --config $configPath
}
```

For an A/B comparison, run each source against these same fixtures and config.
`-p:MchSyncRotationSource=C:/absolute/path/MCH_SyncPlus.cs` selects the alternate
production source at build time without editing the working tree. Keep source
hashes with the reports; the simulator boundary and model must remain identical.

Verify the committed fixtures and untouched historical counterparts:

```powershell
$fixtureRoot = 'tests/FightSim/timelines/native-opportunity'
$provenance = Get-Content -Raw "$fixtureRoot/provenance.json" | ConvertFrom-Json
foreach ($fixture in $provenance.fixtures) {
    $corrected = (Get-FileHash -Algorithm SHA256 "$fixtureRoot/$($fixture.file)").Hash
    $original = (Get-FileHash -Algorithm SHA256 "tests/FightSim/timelines/$($fixture.file)").Hash
    if ($corrected -ne $fixture.sha256 -or $original -ne $fixture.originalSha256) {
        throw "Fixture hash mismatch: $($fixture.file)"
    }
}
```

The original final comparison used baseline Sync source SHA256
`835CD11B3BBB58BDE3E536D358FB25BACDF721D0208A86D3BB22985ED08B5259`
and corrected source
`1A9C479EAC3395D44DC2939BFCC0FDDF81F9CE875280007F24382FF795255FD7`.
It modeled 271.959882 -> 272.838087, 282.372984 -> 282.372984, and
277.021275 -> 277.081788 potency/second for pulls 7, 8, and 10 respectively,
using the then-saved Trust-party-buffs MCH Plan configuration and 0.25 GCD queue.
Those are historical model results, not unconditional expected outputs for a
different configuration. The original incomplete pull 8 remained -0.143% against
that baseline and is retained above rather than overwritten.
