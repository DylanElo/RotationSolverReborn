# FightSim

Runs the real BRD Plan rotation (`BardRotation.cs`, `BRD_Reborn.cs`, `BRD_Plan.cs`, `FRUPad.cs`, compiled from this
checkout) through a whole logged FRU kill and reports its expected DPS next to the log's, overall, per phase and per action.

```powershell
python -I tests/FightSim/timeline.py <report code> <fight id> "<player name>"     # once per log -> tests/FightSim/timelines/
dotnet run --project tests/FightSim -- tests/FightSim/timelines/fru-jD6z47YVBXrCbK9F-5.json [--seeds 20] [--trace] [--config RotationSolver.json]
```

To judge a rotation change, run before and after on the same timelines. 20 seeds take about a minute. The seed-to-seed
spread is ~1% of DPS, so the mean is good to ~0.3%.

## How it works

- **Rotation**: the production files compile unchanged against `Fakes.cs`, the slice of RotationSolver.Basic and Dalamud
  they touch. `CanUse`, `BasicCheck`, `CooldownCheck`, status timing, `GetTTK`/`AverageTTK`, the GCD/weave dispatch and the
  two-weave budget are copied from RSR's source. `gen.py` writes `Generated.g.cs` (action properties and the `ActionID`/`StatusID`
  enums); re-run it when a rotation uses a new action or status. Your `RotationSolver.json` supplies the rotation settings
  and per-action configs (AoE counts, DoT refresh GCDs, disabled actions).
- **Fight** (`timeline.py`, from FFLogs via the xivanalysis proxy): enemy targetable spans, HP, positions and damage-taken
  (Ice Veil 0.5), the player's position and target choices, party raid buffs on the player, lockouts (stuns, Deep Freeze,
  P4->P5 knockdown, death until the next cast, Weakness 0.75), enemy casts (Fatebreaker's enrage, for the pad). BRD Plan's
  windows open 2s before each burst of the party's raid-buff casts, and include a pot when the log potted there.
- **Game** (`Game.cs`, `Bard.cs`): GCD, haste, recast groups and charges, animation lock 0.65s, statuses, songs and
  Repertoire (80%/3s), Soul Voice, Hawk's Eye (35%), Barrage, Codas and Finale, DoT snapshots on the 3s tick, autos.
  Damage is expected value: potency x buffs x expected crit/DH from the log's stats. Potencies are measured from the
  log, not taken from tooltips.
- **Log column**: each logged hit with its crit/DH luck removed, put on the same potency scale (Burst Shot = 220).

## Validation (2026-10-16, rotation source as of that day)

| Kill | Sim | Log | Diff | Worst phase |
|---|---|---|---|---|
| jD6z47YVBXrCbK9F#5 (10-15, no death) | 248.9 | 245.8 | +1.3% | P2 +4.8% |
| XJpcY8aNzDnKkhL6#7 (10-15, P3 death) | 238.2 | 243.7 | -2.3% | P4 -6.9% |

The sim runs today's rotation; the logs ran the 10-15 build. Part of each difference is real rotation change.

## Not modelled

- Phase timings are fixed by the log. Your damage does not move HP checks or phase ends.
- Movement and range: you are always in range of a targetable enemy. Uptime lost to mechanics is not modelled (~19s in the clean kill).
- Enemy hitboxes are assumed (FRU bosses 5y, adds 1y). Only enemies the logged player targeted count as hostiles.
- Mitigation/utility weaves (Troubadour, Minne, Warden's) come from BossMod plans and are not cast. Your config disables Troubadour and Minne.
- Potion +7%, Radiant Encore with 2 Codas (900) and the log's Finale Coda count (3) are assumptions. 2-Coda Encores never appeared in the logs.
- BRD only. MCH needs its own job model (heat, battery, Queen, Wildfire, tools).
