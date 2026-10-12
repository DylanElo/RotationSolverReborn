# MCH outcome scorer

Standard-library Python checks inspired by [xivanalysis's MCH modules](https://github.com/xivanalysis/xivanalysis/tree/f532855e635bdfb4211cec8128d582dadfdc6a75/src/parser/jobs/mch/modules). It reports observed outcomes, not an overall rotation grade.

From the repository root:

```powershell
python -m unittest discover -s tools/mch_score -p 'test_*.py' -v
python tools/mch_score/mch_score.py iinact 'C:\path\Network_30301_20261011.log' --min-length 60 --json artifacts/mch-results.json
python tools/mch_score/mch_score.py fflogs REPORT FIGHT --json artifacts/mch-results.json
python tools/mch_score/mch_score.py cache tools/mch_score/cache/REPORT-FIGHT.json --offline
python tools/mch_score/mch_score.py capture 'C:\path\mch_pulls.json'
```

Use `--player-name` for IINACT (or set the `MCH_SCORE_PLAYER` environment variable) or `--player-id` for FFLogs. Multiple input files or report/fight pairs are supported. The IINACT adapter currently selects FRU only. The FFLogs adapter supports other encounters, enriches old caches additively with metadata/debuffs/deaths, and refuses ambiguous player selection. `--offline` makes no network requests and labels unavailable evidence. Original cached streams are retained.

## What is measured

- **Wildfire:** actual carrier identity and instance; six status stacks when recorded, otherwise distinct landed direct weaponskill hits. Window timing comes from status application/removal when available. Detonation damage is separate from hit count. Death/pull-end interruptions and missing evidence are separate outcomes.
- **Hypercharge:** qualifying casts (Blazing Shot, Heat Blast, Auto Crossbow) within Overheated. Five is the target; this is explicitly a cast count, not a damage count.
- **Queen:** confirmed owned-pet damage and Crowned Collider, with death/pull-end interruption and unknown ownership separated. Attribution currently uses a 20-second summon window; delayed results outside that bound require manual review.
- **Reassemble:** consumption associated with a GCD within 100 ms of status removal. Tools include Excavator. Expiration is not credited as consumption. Scattergun requires target-count context.
- **Potion:** action counts and damage landing during Medicated are reported separately. Neither damage timing nor these counts prove the potion was included in the action's buff snapshot. No universal potion pass/fail rule is imposed.
- Gauge overcap and GCD uptime remain **estimates** under `estimates`; they are not used to certify execution.

IINACT decoding follows the [cactbot LogGuide](https://github.com/OverlayPlugin/cactbot/blob/main/docs/LogGuide.md): direct damage requires a matching target/sequence ActionSync record. Unconfirmed packets remain unconfirmed. Wildfire periodic records carry their own source and damage. Cast-only legacy extracts cannot establish damage or pet outcomes.

## Interpretation

Missing finisher/detonation findings need target life, phase transitions, death, and build-version checks before attribution to the rotation. This scorer cannot establish whether a cast was manual or selected by RSR, and does not grade BMR plan adherence. Keep runtime traces and loaded-build timestamps alongside results. Historical pulls cannot validate later fixes.
