# MCH changelog (this fork)

## 2026-10-12 — BRD songs ahead of damage oGCDs (v7.5.6.32)

- Song switches run first in AttackAbility. RotationSolver checks AttackAbility before GeneralAbility, so songs lost
  every contested weave: the FRU clear switched Army's to Wanderer's 8-11s late, burst in Army's Paeon, and the
  drift carried into P5 (first burst in Mage's Ballad). Top logs: Wanderer's first, then the buffs.
- Wanderer's moves to Ballad with 1s left even if Repertoire remains (it ran out at 45.6s in the clear).

## 2026-10-12 — BRD source audit fixes (v7.5.6.31)

- DoTs are applied before Ladonsbite / Quick Nock. With AoE at 2 the FRU P4 pair never got DoTs, and Battle
  Voice waits for both DoTs, so P4 would never have burst.
- Empyreal Arrow and Sidewinder wait for Raging Strikes only while burst is on (Auto Burst off stranded both).
- A held Blast Arrow no longer aborts the GCD when a DoT is about to drop.
- Mid-Ballad Apex fires from 22s left on the song (the 18s check was a typo).

## 2026-10-12 — BRD Reborn audit fixes (v7.5.6.30)

- No song switch in a song's first 10s (two FRU openers switched Wanderer's to Ballad 0.5s in).
- Damage oGCDs are held for the Battle Voice / Radiant Finale pair only when Finale is back within a GCD.
- Pitch Perfect at 3 stacks goes before Empyreal Arrow; Radiant Encore fires before Encore Ready expires.
- Apex: capped gauge in Wanderer's spends when Raging Strikes is 10s+ away; threshold checks are "at least".
- Burst charge spending tries Rain of Death before Heartbreak Shot.
- Earlier today (v7.5.6.28): Caustic/Venomous Bite status checks fixed.

## 2026-10-12 — Reassemble always lands on a tool (v7.5.6.29)

- Reassemble is pressed only when the next GCD is a tool that will be ready by then.
- With Reassemble up, the next GCD is a ready tool (Air Anchor > Drill > Excavator > Chain Saw) before the
  expiring-combo save, which could spend it on Heated Clean Shot.
- `tools/mch_score` rewritten: reads IINACT logs directly, outcome checks modelled on xivanalysis MCH.

## 2026-10-12 — Barrel before capped Hypercharge, potion grace, Detonator carrier (v7.5.6.27)

- A capped-Heat Hypercharge no longer jumps ahead of a ready, committed Barrel Stabilizer. 7 of 172 logged Barrels
  (all the P4 burst) came right after one, and Wildfire then landed 8.2s after Barrel instead of 3.5s.
- MCH Plan arms the late-potion grace only once a potion window has opened; arming it on a future window let any
  earlier raid buff spend the potion.
- The early Detonator reads the enemy carrying the Wildfire, not the currently selected target.

## 2026-10-12 — Detonator and Queen Overdrive on dying crystals (v7.5.6.26)

- Detonator fires when the recent-HP-loss rate says the Wildfire target dies within 2s and before the natural
  detonation. GetTTK lags ~2s, so 8 of 49 logged crystal Wildfires detonated on a dead crystal for 0 damage;
  the old TTK<=1s trigger never fired in a logged pull.
- Queen Overdrives when its target dies within 3.5s, even with other enemies up: only 6 of 21 crystal-phase Queens
  landed Crowned Collider by moving on to the next crystal.
- MCH plan (BMR side): the crystal potion window starts 6s later, so the potion lands with Wildfire instead of 7-10s
  before it.

## 2026-10-09 — Crystal Wildfire guard only holds under 8s (v7.5.6.25)

- The recent-HP-loss guard now holds Wildfire only when it reads under 8s (was 10s). Burst openers make the rate
  read fast: one pull read 7-9s on a crystal with 13-16s left and lost the window's Wildfire. Across 45 logged
  crystal Wildfires the 10s line stopped 6 bad ones but held 6 good ones; 8s stops 4 and holds none.

## 2026-10-09 — No Wildfire on a crystal about to die (v7.5.6.24)

- The dying-target Wildfire guard also checks the HP lost over the last ~4s. RSR's TTK averages the whole history
  and lags ~2s: one crystal read 10.3s on a crystal that died in 6.9s, and the Wildfire detonated on nothing
  (also #24, #26; partial #22, #41).

## 2026-10-08 — Early partial buffs wait for the planned window (v7.5.6.23)

- MCH Plan: partial raid buffs ahead of a planned window bank instead of firing; the overlap wait counts from the
  window opening. Full buffs still fire at once; late buffs within 30s after a window fire after the wait.
  Live case: P4 Wildfire on 2/4 buffs (early Divination), ~7s before the party burst.

## 2026-10-08 — Audit fixes (v7.5.6.22)

- MCH Plan waits for the rest of the raid buffs like MCH Sync (partial-overlap wait), unless downtime is too close.
- Queen "next target" ignores enemies that are dying too (P4 pair), for Overdrive, Barrel hold and lifetime hold.
- A potion window's potion is kept for 30s after the window ends, so late buffs still get it.

## 2026-10-08 — Ice Veil end counts as downtime everywhere

- `DowntimeWithin` now includes the Ice Veil's cast-bound end (it only fed `DowntimeIn` before). The Queen guard,
  Barrel and Wildfire blocks use it; FRU pulls B/C summoned a 50-Battery Queen 4.4s before the veil vanished and
  overdrove it 3s later.

## 2026-10-08 — Barrel Stabilizer target-life hold

- Barrel Stabilizer is held when the target dies before a Wildfire payload would land and no other enemy is up
  to take the Hypercharge / Full Metal Field (`ShouldHoldBarrel`; logs had it pressed seconds before an M3 kill).

## 2026-10-08 — Dungeon logic in Trust party buffs

- "Trust party buffs" now runs the dungeon trash/boss tracker too (one trash burst, then bank; Wildfire only on
  bosses). Unlike Automatic it keeps party sync in dungeons, so a real party's buffs are still waited for.
  Meso Terminal evidence: on Trust the Headsman got Wildfire 74–82s in; on Automatic 9–12s.

## 2026-10-08 — Wildfire and Barrel only in a confirmed burst

- With party sync on (MCH Sync Automatic/Trust, MCH Plan), Wildfire and Barrel Stabilizer commit only on live
  raid buffs or an open vulnerability window. Removed: the 30s hold ceiling release, the "last full window before
  downtime" release, and the "no reason to wait" default commit. Tools, Queen and Hypercharge keep their normal
  rules. Partial buffs with a phase change too close to wait for the rest still commit.
- MCH Plan: windows set when to bank the gauge; live raid buffs (even late ones) are what fire the burst and the
  planned potion. "Extra Wildfire between windows" removed.

## 2026-10-08 — MCH Plan

- **New rotation "MCH Plan"** (`MCH_Plan.cs`): MCH Sync with burst windows read from the BossMod Reborn plan
  (Utility: MCH → Burst track: `Window` / `WindowWithPotion`). A window opens at its planned time, or up to 8s
  early when raid buffs go up; Wildfire between windows only when it is back before the next one; potions only in
  potion windows. Falls back to MCH Sync where no plan is loaded. Scheduler: `MCHPlanScheduler` (pure, tested).
- **BossMod fork**: `ClassMCHUtility` gains the `Burst` marker track; `PlanExecution.GetUpcomingPlannedActions`
  reports it with no action, so RSR's plan updater never auto-casts it.
- **FRU plan** (`12f45bae…json`): 8 windows anchored to states — P1 2:06, P2 4:08, crystals 6:12 (pot),
  P3 8:28, P4 10:46 (pot, after Darklit; pot cooldown from the crystals), P5 start, P5 +2:00 (pot), P5 +4:00.
- **Replay scorer** (`tools/mch_score`): Wildfire GCDs/buffs/lost, potions, Queen Battery and cancels,
  estimated overcap, Reassemble targets, GCD uptime — from FFLogs (xivanalysis proxy) or IINACT captures.

## 2026-10-08 — MCH Sync fixes from the FRU logs

- Phase objectives (FRU): bank gauge and Wildfire once a timed HP check is secured (P2 < 18%, P3 < 17%) or a
  carry-over target dies within 10s; tools still fire into a kill.
- Queen is not cancelled or held back while another enemy remains (crystal → Ice Veil).
- No partial raid-buff wait on non-boss targets (crystals).
- Ice Veil lifetime bound to Endless Ice Age; no Wildfire on a dying target.
- Reassemble waits for a banked Drill instead of landing on a filler.
- Third (10:25) and fourth (16:00) potion options.
