# FightSim

Runs the real BRD Plan or MCH Plan rotation (`BardRotation.cs`, `BRD_Reborn.cs`, `BRD_Plan.cs`; `MachinistRotation.cs`,
`MCH_Reborn.cs`, `MCH_SyncPlus.cs`, `MCH_Plan.cs`; `FRUPad.cs`, compiled from this checkout) through a whole logged FRU pull
and reports its expected DPS next to the log's, overall, per phase and per action.

```powershell
python -I tests/FightSim/timeline.py <report code> <fight id> "<player name>"     # once per log -> tests/FightSim/timelines/
dotnet run --project tests/FightSim -- tests/FightSim/timelines/fru-jD6z47YVBXrCbK9F-5.json [--seeds 20] [--trace] [--config RotationSolver.json]
dotnet run --project tests/FightSim -- tests/FightSim/timelines/fru-mch-jJXkAtKaFQPyH4Wn-27.json [--rotation MCH_SyncPlus]
dotnet run --project tests/FightSim -- --check-downtime
```

The job comes from the timeline (`"job"`, written by timeline.py from the player's FFLogs job; BRD when absent). The rotation
defaults to BRD_Plan / MCH_Plan; `--rotation` takes any rotation class name. MCH has no procs, so its seeds are identical.

## HP checks
Every run ends with an `HP check` block for the timed phases whose boss only has to be under 20% when the phase timer runs out
(P2 Usurper, P3 Oracle). The log's boss HP at the end of the window is moved by the sim's difference in this player's damage over
the window (real damage per potency from the log's own hits, divided by the boss's HP pool); the party's damage stays the log's.
`Slack` is how many HP points under 20% it ends: 10-21's P2 had 1.2, the other MCH pulls 2-7. A change that raises DPS but drops
slack below zero fails the check in a real pull.

## Focused MCH Queen / P5 decisions

```powershell
dotnet run --project tests/FightSim -c Release -- --check-mch-queen
dotnet run --project tests/FightSim -c Release -- --check-mch-phase-entry
dotnet run --project tests/FightSim -c Release -- --check-mch-preparation
dotnet run --project tests/FightSim -c Release -- --check-mch-queen-model

# The same final boundary/model can compile a saved pre-fix Sync source to reproduce RED.
dotnet run --project tests/FightSim -c Release -p:MchSyncRotationSource=C:/absolute/path/MCH_SyncPlus.cs -- --check-mch-queen
```

The decision mode calls the complete production `MCH_Plan` / `MCH_SyncPlus` / `MCH_Reborn` selectors, including
`NextAbility`, `GeneralAbility`, `EmergencyAbility` and `NextGcd`. It executes selected actions and observes the resulting
gauge, statuses, cooldowns and damage. Cases cover stale P2 return forecasts, confirmed versus previewed pets, actual hostile
fallback, Queen lifetime and follow-up targets, terminal Detonator priority over Queen Overdrive, confirmed CT Heat
reservation and resets, and P4 -> CT -> P5 execution. Viable paid P4 Hypercharge remains available at 50/60/95 Heat.
Barrel previews cannot arm the reservation; a confirmed free status requires the bounded CT return window.
The CT cases retain available 20 or 50 Heat through Barrel, Full Metal Field and five free Blazing Shots.
The funded 50-Heat P5 case additionally requires Wildfire before Barrel returns,
five Blazing Shots in the live raid buff, and an actual six-hit Wildfire detonation. The model mode separately checks
natural/early/late Overdrive game events and forbids duplicate Queen finishers.

Return preparation is exercised through an actually observed attackable phase boss followed by native target loss.
Valid CT and unfinished P2 returns use the exact actor's current object-table identity and HP, including when the raw
target is cleared or unrelated. P2/P3 preparation requires HP above the actual 20% phase-pass check; the lower offensive
banking margin is a separate policy. Completed checks, missing/replaced objects, unknown phases and unknown forecasts
must refuse the summon. Focused fixture reset preserves native action configuration defaults, including Queen's TTK 16.

These checks also execute Drill -> Queen at 90 Battery -> Air Anchor for 20 Battery without overcap, check the one-GCD
failed-weave watchdog and reject unsafe forecast-only lifetimes. `--check-mch-phase-entry` is an alias of the complete
Queen decision mode. Rejected warming/P3-banking experiments are archived with their artifacts and are not active test modes.

`--check-mch-preparation` separately exercises the two observed transition preparations: an actually present full-HP P2
body after the observed P1 actor reaches zero, and completed P2 Absolute Zero (40224) followed by the bounded 22.1s
crystal-return anchor. It rejects absent/future/replaced objects, inappropriate phases, cancellation, unknown casts,
invalid completion HP, death, territory changes, clock rollback and expired anchors. Native `AllTargets` filters targetable
objects; the object-table boundary also includes present untargetable actors. `TimelineActor.PresentFrom` optionally supplies
observed object creation time. Null keeps legacy fixtures unchanged; targetability alone cannot prove creation time.

The decision log prints SHA-256 fingerprints of the three production MCH sources embedded in the compiled assembly.
`MchSyncRotationSource` changes only the source being compiled for the comparison; the action/model boundary stays the same.
The external boundary supplies job gauge, action availability, target/status/cooldown facts, party buffs and BMR forecasts.
It models native `CanUse`; it does not invoke Dalamud or the game action manager. Optional focused-test boundary overrides
reset before each fixture and leave ordinary whole-fight defaults unchanged. Cooldown raw times use the base action's
fixed native cooldown group, while adjusted IDs supply charge facts. These are production decision regressions and replay
model checks, not live action issuance, movement or Queen retarget/chase proof.

Object-table lookup defaults to timeline actors; recorded targetability is independent. These cached timelines do not
record object removal, so continued presence is a replay assumption applied equally to compared production variants.
Focused tests override object lookup explicitly to exercise actual removal and replacement safety.

To judge a rotation change, run before and after on the same timelines. 20 seeds take about a minute. The seed-to-seed
spread is ~1% of DPS, so the mean is good to ~0.3%.

## FRU P1 pad

```powershell
dotnet run --project tests/FightSim -c Release -- --check-fru-pad
```

The real `FRUPad.Padding` on the sim's 1/s HP record and cast replay (35 checks). Synthetic HP curves cover the enrage
cast, the pre-cast 160s fallback (9.7s floor), the 5% / last-5s / NaN guards, non-FRU and other targets. The logged P1 ends
of the six MCH timelines pin what it does to real pulls: it never holds in AWX-7/8/10 and jJX-5/27, and holds only
149.5-153.6s in gNf-3 (the party killed Fatebreaker at 160.0, 1.6s before the cast ends). After the cast bar ends the
fallback applies again; that is a wipe either way, so the replay stops at the cast end.

## Final-kill dump

```powershell
dotnet run --project tests/FightSim -c Release -- --check-final-kill
```

Short synthetic fights where Pandora (0x45AF) dies at a known time, every cooldown drained except the one resource under
test (Heat, Battery, Barrel, Excavator, Wildfire; Battle Voice/Finale, Soul Voice, Pitch Perfect, Heartbreak, Sidewinder).
The real BRD_Plan / MCH_Plan run through the whole-fight GCD/weave loop with rotation defaults (not your RotationSolver.json),
and each resource must be spent before the death (Wildfire detonated with stored hits, Queen's Crowned Collider landed).
Each case runs again on the P4 pair, where FinalKillIn must stay NaN. It fails with one HOLD line and trace per hold.

## How it works

- **Rotation**: the production files compile unchanged against `Fakes.cs`, the slice of RotationSolver.Basic and Dalamud
  they touch. `CanUse`, `BasicCheck`, `CooldownCheck`, status timing, `GetTTK`/`AverageTTK`, the GCD/weave dispatch and the
  two-weave budget are copied from RSR's source. `gen.py` writes `Generated.g.cs` (action properties and the `ActionID`/`StatusID`
  enums); re-run it when a rotation uses a new action or status. Your `RotationSolver.json` supplies the rotation settings
  and per-action configs (AoE counts, DoT refresh GCDs, disabled actions).
- **Fight** (`timeline.py`, from FFLogs via the xivanalysis proxy): enemy targetable spans, HP, positions and damage-taken
  (Ice Veil 0.5), the player's position and target choices, party raid buffs on the player, lockouts (stuns, Deep Freeze,
  P4->P5 knockdown, death until the next cast, Weakness 0.75), enemy casts (Fatebreaker's enrage, for the pad), the party's jobs (RSR's `Buffs`), Mug/Dokumori/Chain Stratagem
  on enemies, the pet's (Queen's) hits. The plan windows (BRD Plan's, MCH Plan's `ClassMCHUtility` Burst track) open 2s before each burst of the party's raid-buff casts, and include a pot when the log potted there.
- **Game** (`Game.cs`, `Bard.cs`, `Machinist.cs`): GCD, haste, recast groups and charges, animation lock 0.65s, statuses, autos.
  BRD: songs and Repertoire (80%/3s), Soul Voice, Hawk's Eye (35%), Barrage, Codas and Finale, DoT snapshots on the 3s tick.
  MCH: Heat (+5 per combo GCD, Hypercharge 50 or Hypercharged), Overheated (5 stacks / 10s, Blazing Shot on a 1.5s GCD,
  -15s Double Check and Checkmate each), the 1-2-3 combo (30s; RSR's `ComboIds` check), Battery (+10 Clean Shot, +20 Air
  Anchor / Chain Saw / Excavator, cap 100), Reassemble (2x55s), Drill (2x20s), Chain Saw (line, cleaves) -> Excavator Ready,
  Barrel Stabilizer -> Hypercharged + Full Metal Machinist, Wildfire (stacks per weaponskill on its target, snapshot at cast,
  Detonator), Automaton Queen (fixed 17.4s run: 5 Arm Punches, Pile Bunker, Crowned Collider, scaled by Battery, hitting the
  current target; Overdrive drops the punches and fires the finishers). Damage is expected value: potency x buffs x expected
  crit/DH from the log's stats; auto-crit/DH hits (Reassembled, Full Metal Field) use crit x 1.25 plus Battle Litany's
  conversion; Wildfire never crits. Potencies are measured from the log, not taken from tooltips.
- **MCH potencies** (jJXkAtKaFQPyH4Wn#27, luck removed, party buffs divided out, Heated Split Shot = 220): Slug 320, Clean 420,
  Blazing Shot 260, Drill / Air Anchor / Chain Saw / Excavator 660, Full Metal Field ~980, Double Check / Checkmate 180,
  Wildfire 240 per weaponskill, Queen ~24 per Battery (Arm Punch 2.15, Pile Bunker 6.2, Collider 6.9 per Battery; per summon
  against the Battery rebuilt from the logged casts), autos 58 every 2.67s, GCD 2.50.
- **Log column**: each logged hit (and the Queen's) with its crit/DH luck removed, put on the same potency scale
  (BRD: Burst Shot = 220; MCH: Heated Split Shot = 220).
- **BossMod forecasts**: `DowntimeForecast.cs` mirrors FRU's four state-machine phases (P1, P2, combined P3/P4, P5).
  Start and end flags are searched independently in the current phase; unknown is `float.MaxValue`. Kill-triggered phase
  changes and the terminal wipe are not predicted downtime starts. CT return is known, but P5 return is hidden until P4
  ends. Ultimate Relativity has downtime flags from Return stun to Shell Crusher's cast start even while Oracle is targetable.
  Actual targetability and player lockouts still come from the log, independently of the forecast.
- **Plan visibility**: with `planHiddenAcrossKills`, next-phase windows are hidden until the preceding kill enters that phase,
  not until its boss becomes targetable. MCH/BRD Plan retain their production 999s stand-in while no window is visible.
  The older BRD timelines omit this setting and continue to expose every plan window; their downtime forecasts still use the corrected model.
- **Forecast precision**: flag times use logged mechanic/targetability events and phase entries use the preceding phase's last
  targetable span. This models information availability, not BMR's exact per-state nominal countdowns or IPC polling delay.
  Truncated logs can omit a future mechanic BMR would know. Unknown forecasts do not disable the active module or its IPC endpoints.

## Earlier validation (2026-10-16, before corrected BossMod forecasts)

| Kill | Sim | Log | Diff | Worst phase |
|---|---|---|---|---|
| jD6z47YVBXrCbK9F#5 (10-15, no death) | 248.9 | 245.8 | +1.3% | P2 +4.8% |
| XJpcY8aNzDnKkhL6#7 (10-15, P3 death) | 238.2 | 243.7 | -2.3% | P4 -6.9% |

The sim runs today's rotation; the logs ran the 10-15 build. Part of each difference is real rotation change.
With the BRD sources as of 2026-10-16 01:38 (BRD_Reborn edited after the table above): 249.2 / +1.4% and 238.0 / -2.3%.

MCH (2026-10-16, with RSR's GCD queue: the next GCD commits at 0.25 x GCD remaining, `_action6headDict`). The user has no MCH FRU kill on FFLogs (shikatsu18's MCH "kills" are dungeons), so these are the two
longest MCH pulls, both P5 wipes on the 10-12 build. The death at the wipe locks the sim out too.

| Pull | Sim | Log | Diff | P1 | P2 | P3 | P4 | P5 |
|---|---|---|---|---|---|---|---|---|
| jJXkAtKaFQPyH4Wn#27 (P5 7.6%, 1147s) | 287.1 | 288.5 | -0.5% | +0.1% | -0.5% | +0.4% | -3.6% | +0.7% |
| jJXkAtKaFQPyH4Wn#5 (P5 16%, 1050s, NIN+SCH) | 294.1 | 301.8 | -2.5% | -2.7% | +0.4% | +0.5% | -9.4% | -2.0% |

Cast counts match the logs within 1-2 per action. Two differences are rotation changes since 10-12, not sim error:
- Reassemble goes to Drill/Air Anchor where the log put it on Chain Saw/Excavator (`SelectReassembledTool` takes Drill
  first when both are up). Same potency single-target, so the per-action table shifts but the total does not.
- #5 P4/P5: today's P4 Barrel-on-cooldown (628s) is back at 748s, inside the CT Stun, so the CT Barrel goes at the Stun's end
  (755s) and is back at 876s, after P5's opening buffs (861-883s). P5's opener then has no Barrel and its Wildfire goes at
  877s, at the end of the buffs. In #27 the Barrel was back at 743s, before the Stun, and P5 lines up (Barrel 867, Wildfire 870).

## Corrected forecast rerun (2026-10-16)

`--check-downtime` passes 35 assertions covering phase entry, independent start/end flags, UR, CT, terminal wipes,
plan visibility and both MCH fixtures. These results supersede the earlier MCH table for the corrected model.
Both pulls use the current saved RSR config, MCH_Plan and one deterministic seed. Values are expected potency/second,
not measured in-game DPS. Full traces and diagnostic provenance are in `artifacts/frusim-downtime-20261016/`.

| Pull / policy | Whole fight | P4 | P5 | P5 Wildfire (seconds) |
|---|---:|---:|---:|---:|
| #5 current | 292.2 | 232.7 | 376.4 | 876.69 |
| #5 reserve late CT Barrel through both paths | 293.0 | 224.0 | 391.7 | 866.68 |
| #27 current | 288.5 | 228.2 | 342.2 | 884.68 |
| #27 reserve late CT Barrel through both paths | 285.7 | 215.4 | 341.9 | 869.67 |

The reservation variant is an isolated diagnostic: it uses future plan knowledge to suppress late CT Barrel both in
the downtime helper and the P4 on-cooldown path. It is not a production patch. Earlier Wildfire trades away CT damage:
#5 gains about 0.3% overall, while #27 loses about 1.0%. There is no evidence here for a blanket CT Barrel suppression.
The corrected UR forecast shifts #27's earlier Barrel cadence, so its CT Barrel also falls after the stun now.
Use realistic forecast visibility for further comparisons; the old perfect-downtime model misclassified that case.

Blocking only the downtime Barrel path shifts the cast to CT return through the P4 on-cooldown fallback. Although total
model potency rises slightly in both pulls, both variants lose one Wildfire (#5's second P5 use, #27's opening P5 use).
This is not a safe alignment fix. See `artifacts/frusim-downtime-20261016/INVESTIGATION.md` for all six runs and provenance.

## BRD corrected forecast assessment (2026-10-16)

Both BRD clear snapshots were copied to `artifacts/brd-corrected-sim-20261016/` with only
`planHiddenAcrossKills=true`, so these runs exercise the corrected phase visibility as well as downtime.
Original historical fixtures remain unchanged. Current BRD_Plan and the saved RSR config were replayed with 200 seeds.

| Clear | Current whole-fight potency/s | Log | Early CT Finale variant | Paired personal gain |
|---|---:|---:|---:|---:|
| jD6z47YVBXrCbK9F#5, clean | 249.085 | 245.8 | 249.458 | +0.150% |
| XJpcY8aNzDnKkhL6#7, P3 death | 238.133 | 243.7 | 238.441 | +0.129% |

The current 7–10s standalone CT Finale interval falls entirely inside the recorded Return stun. A temporary 12.2–15s
variant enables CT Finale/Encore but leaves only two Codas for the first P5 Finale: its party buff falls from 6% to 4%.
Army's Paeon becomes ready during Down for the Count, preventing that Coda's recovery. The personal gain is reproducible
under paired seeds, but FightSim does not score the rest of the party, so it cannot establish a raid damage gain.
No production change follows from this comparison alone. A late 0–3.15s variant did not cast CT Finale in these replays.

A separate 20-seed temporary target-resolution sensitivity improved P4 somewhat but did not explain its remaining deficit.
Current nine planned buff uses complete in both baseline traces; no broad burst/DoT/Apex/Sidewinder retune was justified.
See `artifacts/brd-corrected-sim-20261016/INVESTIGATION.md` for phase tables, exact counterfactuals, hashes and limitations.

## CT song-cadence regression and damage comparison (2026-10-16)

The later deployed CT fix supersedes the early-window experiment above: it reserves Army and rebuilds three P5 Codas.
The next investigation traced its 49/200 CT skips to P1's exhausted downtime weave budget delaying Ballad and later Minuets.
Compare that deployed source and the P1 song-weave candidate using the same corrected damage model; results are under
`artifacts/brd-ct-optimize-20261016`. The model now includes the Bard's song auras, two-Coda Encore800, Barrage Shadowbite300,
and Pitch Perfect's secondary-target damage, checked against the [official Bard guide](https://na.finalfantasyxiv.com/jobguide/bard/).

`--check-ct-codas` now requires exactly one CT Finale and Encore before the P4 kill in every seed, three Codas on all three
P5 Finales, all planned Battle Voice windows, and preserved P5 song coverage. `--seed-report` emits per-seed JSON damage,
phase totals and casts; `--seed-start N` selects a reproducible seed range. `--check-brd-model` runs isolated emitted-damage
checks for the corrected mechanics. Matching seed numbers do not guarantee matching proc histories after different casts.

`--check-dot-targeting` exercises production BRD DoT predicates through automatic status-action targeting, including
refreshing an eligible second boss without changing the recorded hard target. Other hostile actions retain fixture targeting.
`--check-geometry` checks Apex/Blast's 25y-long, 4y-wide rectangle against enemy hitboxes, including origin overlaps and
corner misses. Lines aim at the selected target because timelines do not record facing. Native manual-mode restrictions,
visibility, and exact hitbox sizes remain outside this replay model.

`--check-reference-calibration` checks that reduced-damage targets do not depress the Burst Shot calibration factor;
normalized reference damage still retains their mitigation. Without hit target IDs, recorded targets resolve overlapping
actors where possible; samples with unknown or mixed damage-taken multipliers are excluded from calibration.
`--check-return-positions` checks return-pose sampling and two-target Encore damage. Enemy positions may use the first
same-span sample up to 500ms after targetability starts, only until that sample's timestamp. Later movement, HP, and
player positions retain chronological sampling; the bounded return pose remains an approximation.

`--check-application-timing` checks delayed damage, target disappearance, originating-cast attribution, cast-time
snapshots, delayed DoT application/duration, reset and the exact replay end. Normal BRD actions and auto-attacks use
packet-matched P4 median delays in `BardApplicationTiming.cs`; buffs, cooldowns, resources and existing self-proc timing
remain cast-based. Geometry is resolved at cast, damage is checked against targetability at each application, and DoTs
start on application with the cast snapshot. `Bard.Execute` calls in isolated potency tests remain immediate; complete
replays and application regressions use `Game.Execute`. MCH application timing is unchanged.

These delays come from one reference clear, rather than a native distance-aware timing model. Recipient order follows
the resolved target list; additional recipients reuse the second-recipient estimate. `--seed-report` includes landed
and dropped damage applications with their originating cast, plus a full-precision `REFERENCE` summary. Finale reference
calibration now infers distinct Codas from logged songs, consumes them at Finale, and clears them on death.

## Not modelled

- Phase timings are fixed by the log. Your damage does not move HP checks or phase ends.
- Movement and range: you are always in range of a targetable enemy. Uptime lost to mechanics is not modelled (~19s in the clean kill).
- Enemy hitboxes are assumed (FRU bosses 5y, adds 1y). Only enemies the logged player targeted count as hostiles.
- By default mitigation utilities are not cast. `--replay-utilities` replays the clear's recorded Troubadour, Minne and Warden casts as utility intents, sharing the production weave budget and deadline policy. Each recorded cast anchors a sensitivity window using its configured native duration (including 0.5s CT Minne and 0.4s P5 Minne). It requires every recorded intent to execute and audits emitted rolling GCD windows for triple weaves. Exact native activation times, targets, eligibility and packet latency are not reconstructed: duration after the logged cast is an approximation of remaining slack. Warden also receives synthetic reservations; native production reserves only resolved default/self Minne and Troubadour. Native request races are checked separately by WeaveBudgetCheck and production binding checks.
- Potion +7% and the log's Finale Coda count (3) are assumptions. Two-Coda Encore now uses the documented800 potency.
- BRD includes Battle Litany, target-specific Chain Stratagem, its song auras and its own Battle Voice in expected crit/DH damage. DoTs snapshot their target's Chain and damage debuffs. Historical logged hits omit target IDs, so the Log column cannot exactly restore target-specific Chain; controlled before/after runs use the same scoring model.
- MCH: Bioblaster's DoT, Roller Dash (same total as the two punches it replaces), Tactician/Dismantle (BossMod plans) and
  movement are not modelled. BossMod forecast visibility is modelled as described above; exact live state countdowns are not.

`--check-ct-recovery` runs stateful production song selection checks for delayed CT Finale status, unconfirmed requests,
repeated Pitch Perfect procs, Finale expiry, a Minuet cooldown later than the generic downtime allowance, actual Ballad
recovery, and the resulting three-Coda P5 Finale. Full clear replays complement these dispatch/state reproductions.

`--check-brd-apex-policy` exercises the production Apex selector around the 25s Battle Voice hold, including capped
Minuet, distant buffs, AutoBurst off, long planned holds, imminent downtime boundaries, active buffs, and final-kill exceptions. CT recovery checks
also cover the raw final half-second of Minuet, queued-GCD timing, and draining the last usable Pitch Perfect.

`--check-target-availability` checks the shared automatic Empyreal target filter and Oracle fallback during FRU CT.
It uses BossMod's global forecast and the encounter's 1.1s disappearance stagger, with the measured 1.025s impact
estimate. Unknown forecasts and manual targeting retain existing behavior; replay damage still uses its independent
targetability spans. The simulator links the production helper used by the native target resolver.

## BRD damage optimization comparison (2026-10-17)

The current model uses native cone geometry, validated against all 37 logged P4 Ladonsbite casts in the three fixtures.
Shadowbite, Apex and Resonant use official 200, 700 maximum and 640 direct potency. The empirical DoT conversion to the
Burst Shot damage scale remains 24/30 rather than raw tooltip 20/25. Apex/Blast line geometry, exact native target selection,
hitboxes and subsecond BossMod state forecasts remain approximations.

The deployed-source baseline and candidate both use these same corrections. `--seed-report` includes all casts and per-action
damage, enabling phase, cooldown-use and paired-seed comparisons. For isolated counterfactual builds,
`dotnet build tests/FightSim -p:BrdRotationSource=<absolute-source-snapshot> -o <isolated-output>` compiles that saved BRD source;
omitting the property compiles the current production source. No live configuration changes are needed.

Exploration used seeds 0–19. Validation used separate seeds 200–399 on each clear, with CT/Coda, utility and weave checks.
Results, immutable snapshots and a reproducible comparison script are under `artifacts/brd-damage-optimize-20261016/`.
