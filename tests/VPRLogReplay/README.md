# VPR log-backed whole-fight policy replay

This harness compiles actual VPR Sync selection/scheduler methods from the requested source snapshot. It advances its own resource/combo/cooldown state throughout all five complete FFLogs fights. The modeled native boundary is deliberately separate from source selection; this is not an in-game action engine or damage simulator.

No new packages: Roslyn comes from the .NET SDK. Requires .NET 10 and Python 3.

```powershell
$study = 'C:\Users\dylan\.codex\visualizations\2026\10\18\01a14df8-f0f4-79e1-9282-279fa2013767\vpr-top-study'
python tests/VPRLogReplay/prepare.py $study "$study\replay\fixtures"
dotnet run --project tests/VPRLogReplay -- --fixtures "$study\replay\fixtures" --source-directory "$study\replay\before-source" --output "$study\replay\before"
dotnet run --project tests/VPRLogReplay -- --fixtures "$study\replay\fixtures" --output "$study\replay\current"
python tests/VPRLogReplay/compare.py "$study\replay" .
```

The preserved pre-change VPR source lives in the study's `replay/before-source` folder; `--source-directory` is mandatory for a historical comparison. Omitting it always uses current source. The fixture directory and output directory are mandatory arguments.

`REPORT.md` in the output's parent records modeled assumptions and limitations, per-fight before/current/reference counts and source hashes. Each run emits summaries and full modeled action timelines. Checks reject illegal casts/resource ranges, incomplete or overlapping Reawaken sequences, and unexplained active-target stalls above two seconds. They do not assert matching top-player action counts or claim rDPS improvement.

External observed Medicated windows are replayed with auto-potion disabled. Exact log-derived targetability/target lifetime approximations and favorable melee/facing are environmental calibration, not evidence that current live predictors or arbitrary movement are modeled. Reference casts never update the simulated gauge/cooldowns or choose its actions. See `REPORT.md` for the remaining assumptions and visible regression tradeoffs.

The timing iteration additionally snapshots `ViperRotation.cs` and `CustomRotation_OtherInfo.cs` beside both VPR files. The harness extracts the actual Vice/Ire readiness expressions and shared FinalKill helper from that same snapshot. It polls production selection each 20ms frame, passes the actual predicted GCD (or a neutral placeholder when none exists) into ability selection, and advances resources only on execution. Coil follow-ups use the 30s venom lifetime; Fury follow-ups use their 60s Poised lifetime. Native adjusted recasts use the same observed per-action median calibration as execution. Dying follows the verified saved/default thresholds: TTK <= 10 seconds or last observed raw target HP below 2%; those HP transitions are external log conditions, not calculated damage from the model. FRU final-kill classification uses the actual helper's Pandora BaseId guard; other phases are not automatically final kills.

```powershell
dotnet run --project tests/VPRLogReplay -c Release -- --fixtures "$study\replay\fixtures" --source-directory "$study\replay\baseline-95EEF" --output "$study\replay\timing-context-before"
dotnet run --project tests/VPRLogReplay -c Release -- --fixtures "$study\replay\fixtures" --source-directory "$study\replay\timing-final-40AB-source" --output "$study\replay\timing-final-40AB-after"
python tests/VPRLogReplay/score.py "$study\replay\fixtures" "$study\replay\timing-context-before" "$study\replay\timing-final-40AB-after" "$study\replay\timing-final-40AB-coverage"
```

Each run records all four source hashes. `score.py` emits per-hit/per-source potency coverage and a clearly hypothetical multiplier sensitivity grid; critical/direct-hit chance buffs stay separate from ordinary damage buffs. No player stats or actual potion benefit are inferred from absent gear metadata. See the generated `COVERAGE.md` and `coverage.json` for assumptions and adverse as well as favorable changes.

The final timing snapshot's Sync SHA256 is `40AB09E5873DCB3F92E61B983C592102712241B2A7EC055727DDDB3ECECB7CA6`, compared with the immediately preceding deployed-policy source `95EEF8B426CC42D423F6EC8E78E743E343CA41B47583E3BF898D97DB7F6F36FE`. All five full fights pass the same resource, sequence-completion and stall checks. Completed Reawaken sequences remain 97; DMU retains one token expiry, and Heavy gains one Fury with gauge loss falling from 60 to 50. Other fights retain their complete action outcomes. The source-separated report retains lower Heavy Brotherhood coverage. These are modeled policy results; no deployment or live-game verification is performed by this harness.

Native territory IDs are fixture context verified from encounter territory data. Actor BaseIds remain raw FFLogs enemy metadata. The final classifier requires the actual territory/actor pair; unknown contexts preserve resources. Timeline diagnostics distinguish shared FinalKillIn from the VPR candidateFinalKillIn certificate. Earlier DMU phase 2 therefore has no final-kill certificate, even though the unchanged shared helper returns an actor-lifetime value.

## Native weave boundary correction (DMU follow-up)

Earlier timing reports are preserved as historical model outputs. The current engine retries an empty ability selection every 20ms and enforces the production shared limit of two confirmed oGCD effects per executed GCD. Queue admission follows the verified native formula: remaining shared GCD time must exceed both 0.5 seconds and the saved VPR Action6Head (0.25) times the current shared GCD recast total. The current executed action duration represents that shared recast total. The 0.65-second modeled effect lock and 0.72-second effect spacing remain calibration assumptions; they are not native constants. Cross-phase BMR downtime-end knowledge is unknown, represented as float.MaxValue, rather than inferred from a future log return.

The corrected installed-policy baseline is `replay/timing-dmu-token-sharedqueue2-before` using the immutable `timing-final-40AB-source`. All five fights pass resource, completed-sequence and stall checks. An independent full-timeline scan in `execution-checks.json` confirms at most two oGCDs per GCD for every fight. Reawaken counts are cruiser20, DMU25, FRU22, heavy12 and light17; token expiries are zero except DMU2. Thus earlier DMU expiry counts and cadence diagnoses are superseded. No new production fix or damage improvement follows from correcting the harness.

## Final DMU token timing comparison

Final Sync source `ECA5D54602B45B754A638F055DE5A1A5427CD9A6683DFDBF232B4B13A71521AA` is captured with the other three source files in `replay/timing-dmu-token-final-source`. Against installed-policy 40AB under the same corrected native queue/two-weave boundary, DMU token expiries fall from two to zero and completed Reawakens increase from 25 to 26. Fury changes 38 to 37, Ire 10 to 9, and Vice remains 28. Every action total and source-coverage result for the other four fights remains unchanged. All five timelines pass illegal-resource, complete-Reawaken and stall checks; independent timeline scans confirm at most two confirmed weaves per GCD.

The full-action normalized potency sensitivity changes by +1581.695 to +2072.507 in DMU across 7,776 hypothetical scenarios; exact burst-family coverage changes by +4992.477 to +5586.179 across 1,944 scenarios. Other fights are neutral. These are bounded model indicators, not actual DPS/rDPS. Raw human-count differences, ideal melee/facing, historical calibration, perfect external boss boundaries, representative normal combos, and modeled animation spacing remain limitations. Cross-phase downtime-return knowledge is not supplied to production selection.

```powershell
dotnet run --project tests/VPRLogReplay -c Release -- --fixtures "$study\replay\fixtures" --source-directory "$study\replay\timing-dmu-token-final-source" --output "$study\replay\timing-dmu-token-final-after"
python tests/VPRLogReplay/score.py "$study\replay\fixtures" "$study\replay\timing-dmu-token-sharedqueue2-before" "$study\replay\timing-dmu-token-final-after" "$study\replay\timing-dmu-token-final-coverage"
```
