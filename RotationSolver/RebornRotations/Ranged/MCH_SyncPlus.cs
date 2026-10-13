using System.ComponentModel;
using ECommons.DalamudServices;

namespace RotationSolver.RebornRotations.Ranged;

/// <summary>
/// Canonical encounter-aware MCH rotation. Burst scheduling is intentionally isolated behind a
/// pure snapshot/decision boundary so live action selection and regression checks share one owner.
///
/// The central change is that burst is scheduled against the encounter timeline rather than a
/// blind 120-second grid. MCH Sync infers a 2-minute cycle from observed raid buffs and
/// extrapolates forward; that inference is exactly what a fight with heavy downtime breaks.
/// BossMod Reborn already publishes when the boss goes untargetable and when vulnerability
/// windows open, and the base rotation consumed only one of thirteen available helpers.
/// <see cref="MCHSyncPlusScheduler"/> reads them and answers two questions per frame - commit
/// Wildfire, and spend or bank flexible resources - which the old design conflated into one.
///
/// The party-buff predictor remains as the fallback path used when no encounter timeline is
/// available.
/// </summary>
[Rotation("MCH Sync", CombatType.PvE, GameVersion = "7.55",
	Description = "Encounter-aware MCH burst using BossMod timelines when available, with safe raid-buff synchronization and bounded holds.")]
public class MCH_SyncPlus : MCH_Reborn
{
	/// <summary>
	/// Combat this long in a dungeon is treated as a boss encounter even when the boss lookup
	/// disagrees. IsBossFromIcon is one BNpcBase rank check with no fallback, and the dungeon
	/// reserve it feeds is the only hold the scheduler's ceiling cannot break - so a miss meant
	/// no Wildfire, no Queen and no second potion for the rest of the dungeon, silently.
	/// So the heuristic stays - but 30s does not separate the two populations. In a logged dungeon trace
	/// the six trash pulls ran roughly 42-54s and every one of them tripped it, while the shortest
	/// real boss ran 2m08s. 90s clears the longest observed trash by 36s and sits 38s under the
	/// shortest observed boss, and it matches Config.BossTimeToKill, which is the number the
	/// framework already uses to mean "this outlives trash". That margin is what makes icon-only
	/// classification safe: the trace cannot prove the icon test caught these three bosses on its
	/// own, but every one of them outran 90s, so a genuine icon miss still resolves here.
	/// ponytail: one dungeon's separation, not a tuned model. Widen if a long trash pull trips it.
	/// </summary>
	private const float DungeonBossCombatFallback = 90f;

	// WildfirePayloadWindow and WildfireLateWeaveWindow are inherited from MCH_Reborn. Both used to
	// be redeclared here with identical values - the weave window under a second name - so the base
	// and the scheduler each carried their own copy of a number they have to agree on.

	/// <summary>How long the rotation may withhold a GCD waiting for the Wildfire Hypercharge.</summary>
	private const float MaximumHyperchargeStall = 1.5f;

	/// <summary>
	/// How long a Hypercharge dump takes to land: five Blazing Shots at their 1.5s recast. Against
	/// WildfirePayloadWindow's 10s this leaves exactly one GCD of slack, which is the single GCD
	/// the Full Metal Field hold in ShouldUseHypercharge is entitled to spend.
	/// </summary>
	private const float HyperchargePayloadWindow = 7.5f;

	private const float PartyBurstCycle = 120f;

	/// <summary>
	/// Approximate seconds Automaton Queen needs to complete its overdrive once summoned. The
	/// generic Spend/Bank split only knows "safe to release" vs "save for later" - it has no idea
	/// Queen's own payload takes far longer than a Hypercharge dump, so a bare Resources==Spend
	/// reading during the downtime dump would send Queen out with no time to finish.
	/// </summary>
	// In one log, Collider damage arrived about 17.5s after summon, after its cast.
	private const float QueenExecutionWindow = 18f;

	#region Config
	[RotationConfig(CombatType.PvE, Name = "Burst strategy",
		Section = "Burst", Order = 100,
		Tooltip = "Automatic uses encounter timelines, predicts future raid-buff windows, and reserves burst for the dungeon boss. Trust party buffs waits for a live raid buff instead of a prediction, and also reserves Wildfire for dungeon bosses. Personal ignores party buffs entirely.")]
	private protected MCHSyncPlusPlan SyncPlusBurstPlan { get; set; } = MCHSyncPlusPlan.Automatic;

	[RotationConfig(CombatType.PvE, Name = "Use BossMod Reborn timeline",
		Section = "Burst", Order = 110,
		Tooltip = "Uses predicted downtime and vulnerability windows when the active BossMod module provides them. Falls back safely when timeline data is unavailable.")]
	protected bool UseEncounterTimeline { get; set; } = true;

	[RotationConfig(CombatType.PvE, Name = "Wildfire timing",
		Section = "Burst", Order = 120,
		Tooltip = "Choose whether Wildfire leads Full Metal Field or follows the Reborn default ordering.")]
	private protected MCHSyncPlusWildfireWeave WildfireWeave { get; set; } = MCHSyncPlusWildfireWeave.EveryBurst;

	[RotationConfig(CombatType.PvE, Name = "FRU: pad Fatebreaker",
		Section = "Burst", Order = 130,
		Tooltip = "FRU P1 must end on Fatebreaker's last cast (its enrage). Under 5% the rotation stops while the party would kill it 5s+ early, and always hits in the last 5s. The Queen and a running Wildfire still deal damage.")]
	protected bool PadFatebreaker { get; set; } = true;

	private bool PaddingFatebreaker => PadFatebreaker && FRUPad.Padding(CurrentTarget, CombatTime);

	[RotationConfig(CombatType.PvE, Name = "Use a second potion",
		Section = "Potions", Order = 210,
		Tooltip = "Allows a later potion when Wildfire is ready inside the configured timing window.")]
	protected bool UseSecondBurstMedicine { get; set; } = true;

	[Range(300f, 480f, ConfigUnitType.Seconds, 1f)]
	[RotationConfig(CombatType.PvE, Name = "Second potion time", Parent = nameof(UseSecondBurstMedicine),
		Section = "Potions", Order = 220,
		Tooltip = "Target encounter time for the later potion. The rotation still waits for a committed Wildfire window.")]
	protected float SecondMedicineTime { get; set; } = 360f;

	// FRU (Mana): pot 2 lands at the crystals (~6:10) and is back for P4 (Darklit ~10:38) - the guide's reason for potting there.
	[RotationConfig(CombatType.PvE, Name = "Use a third potion",
		Section = "Potions", Order = 230,
		Tooltip = "Allows one more potion when Wildfire is ready inside the configured timing window (FRU: P4 opener).")]
	protected bool UseThirdBurstMedicine { get; set; } = true;

	[Range(540f, 900f, ConfigUnitType.Seconds, 1f)]
	[RotationConfig(CombatType.PvE, Name = "Third potion time", Parent = nameof(UseThirdBurstMedicine),
		Section = "Potions", Order = 240,
		Tooltip = "Target encounter time for the third potion. The rotation still waits for a committed Wildfire window.")]
	protected float ThirdMedicineTime { get; set; } = 625f;

	// Top FRU MCH logs pot a fourth time for the second P5 burst (15:56-16:13).
	[RotationConfig(CombatType.PvE, Name = "Use a fourth potion",
		Section = "Potions", Order = 250,
		Tooltip = "Allows one more potion when Wildfire is ready inside the configured timing window (FRU: second P5 burst).")]
	protected bool UseFourthBurstMedicine { get; set; } = true;

	[Range(840f, 1200f, ConfigUnitType.Seconds, 1f)]
	[RotationConfig(CombatType.PvE, Name = "Fourth potion time", Parent = nameof(UseFourthBurstMedicine),
		Section = "Potions", Order = 260,
		Tooltip = "Target encounter time for the fourth potion. The rotation still waits for a committed Wildfire window.")]
	protected float FourthMedicineTime { get; set; } = 960f;

	[RotationConfig(CombatType.PvE, Name = "Show advanced settings",
		Section = "Advanced", Order = 400,
		Tooltip = "Reveals optional synchronization, diagnostics, and timing controls. Defaults are recommended for normal use.")]
	protected bool AdvancedTiming { get; set; } = false;

	[RotationConfig(CombatType.PvE, Name = "Raid-buff anchor", Parent = nameof(AdvancedTiming),
		Section = "Advanced", Order = 410,
		Tooltip = "Automatic accepts a significant overlap. Selecting a job lets only that job move the learned two-minute anchor; all party buffs are still counted for overlap.")]
	private protected MCHSyncPlusAnchorJob AnchorJob { get; set; } = MCHSyncPlusAnchorJob.Automatic;

	[RotationConfig(CombatType.PvE, Name = "Enable burst diagnostics", Parent = nameof(AdvancedTiming),
		Section = "Advanced", Order = 420,
		Tooltip = "Logs decision changes during combat and writes a Wildfire summary after the fight. Leave off for normal play.")]
	protected bool LogBurstReport { get; set; } = false;

	[Range(0f, 45f, ConfigUnitType.Seconds, 1f)]
	[RotationConfig(CombatType.PvE, Name = "Maximum Wildfire hold", Parent = nameof(AdvancedTiming),
		Section = "Advanced", Order = 430,
		Tooltip = "Hard ceiling for delaying a ready Wildfire while waiting for a better window.")]
	protected float MaximumWildfireHoldSeconds { get; set; } = 30f;

	[Range(0f, 10f, ConfigUnitType.Seconds, 0.5f)]
	[RotationConfig(CombatType.PvE, Name = "Partial-overlap wait", Parent = nameof(AdvancedTiming),
		Section = "Advanced", Order = 440,
		Tooltip = "Briefly waits for missing tracked raid buffs when the active buffs can still cover the Wildfire payload.")]
	// Wildfire snapshots the buffs active at CAST, not at detonation: every weaponskill landed
	// during the following WildfirePayloadWindow accumulates potency, but the multiplier applied
	// when that stored potency detonates is fixed the moment Wildfire is committed. A buff still
	// queuing up when Wildfire casts never applies to the payload, no matter how long the
	// detonation is delayed - so waiting for full overlap before commit is not over-conservative,
	// it is the only way those buffs affect the hit at all. (Confirmed against TheBalanceFFXIV's
	// MCH advanced guide; do not shorten this on the assumption the delayed detonation forgives it.)
	protected float OverlapWaitSeconds { get; set; } = 6f;

	[Range(0f, 15f, ConfigUnitType.Seconds, 0.5f)]
	[RotationConfig(CombatType.PvE, Name = "Burst look-ahead", Parent = nameof(AdvancedTiming),
		Section = "Advanced", Order = 450,
		Tooltip = "How far ahead the scheduler considers predicted raid buffs or encounter opportunities.")]
	protected float LookAheadSeconds { get; set; } = 8f;
	#endregion

	#region State
	private float _lastObservedPartyBurstAt = -1f;
	private int _observedPartyBurstCycle;
	private float _partyBurstStartedAt = -1f;
	private float _anchorWorthyStartedAt = -1f;
	private float _wildfireReadyAt = -1f;
	private float _lastEncounterTime = -1f;
	private float _hyperchargeStallStartedAt = -1f;
	private bool _partyBurstWasActive;
	private bool _anchorWasWorthy;
	private bool _wildfireWasReady;
	private bool _hadWildfire;
	private float _interruptionStartedAt = -1f;
	private bool _partyAnchorNeedsRecovery;
	private readonly HashSet<StatusInfo> _observedPartyBuffs = [];
	private string? _expectedPartyBuffs;
	private float _lastPartyBuffScanAt = -1f;
	private bool _lastPartyBuffScanInCombat;
	private int _lastPartyBuffScanVersion = -1;
	private uint _dungeonTerritory;
	private bool _dungeonWasInCombat;
	private bool _dungeonBossEncounter;
	private bool _dungeonBossCompleted;
	private bool _bankDungeonBurst;
	private string _lastDungeonLogState = string.Empty;
	private int _activePartyBuffs;
	private int _expectedPartyBuffsCount;
	private bool _anchorJobActive;
	private int _activeAnchorJobs;
	private int _expectedAnchorJobs;
	private float _shortestActivePartyBuffRemaining = float.MaxValue;
	private MCHSyncPlusDecision _decision = MCHSyncPlusDecision.Default;
	private float _decisionComputedAt = float.MinValue;
	private bool _decisionComputedInCombat;
	private readonly List<MCHSyncPlusWildfireUse> _wildfireUses = [];
	private float _totalHeldSeconds;
	private string _lastReportedReasonKey = string.Empty;
	private string _pendingWildfireReason = string.Empty;
	private int _pendingWildfireActiveBuffs;
	private int _pendingWildfireExpectedBuffs;
	#endregion

	private protected static float EncounterTime => DataCenter.CombatTimeRaw;
	private protected bool PartyBurstActive => _activePartyBuffs > 0;

	/// <summary>
	/// A live window substantial enough to schedule the gauge around, using the same distinct-job
	/// test that guards the two-minute anchor. Raw PartyBurstActive is true for any single tracked
	/// status, so one off-cycle Dokumori on a trash pull would otherwise raise the Queen target to
	/// 100 and hard-hold the Battery behind a window that is not a raid burst.
	/// </summary>
	private bool SignificantPartyBurstActive => PartyBurstActive
		&& MCHSyncPlusPolicy.IsSignificantPartyBurst(_activeAnchorJobs, _expectedAnchorJobs);
	private protected float OverlapWait => IsCurrentTargetBoss
		? MCHSyncPlusPolicy.EffectiveOverlapWait(_shortestActivePartyBuffRemaining, WildfirePayloadWindow, OverlapWaitSeconds)
		: 0f;
	private protected bool FullPartyBurstActive => _expectedPartyBuffsCount > 0
		&& _activePartyBuffs >= _expectedPartyBuffsCount;
	private protected float SecondsInPartyBurst => _partyBurstStartedAt < 0f ? 0f : EncounterTime - _partyBurstStartedAt;
	private float WildfireHeldFor => _wildfireReadyAt < 0f ? 0f : EncounterTime - _wildfireReadyAt;
	/// <summary>
	/// The target every scheduling predicate here reasons about. Named once because the whole
	/// class must agree on it: the five-way copy of the Boss-only Wildfire rule split into two
	/// different target sources precisely because the expression was retyped at each site.
	/// Deliberately NOT the same as the action's resolved target - see WildfireResolvedTargetAllowed
	/// on the base class, which is the post-CanUse authority. This one has to answer beforehand.
	/// </summary>
	private static IBattleChara? SchedulerTarget => CurrentTarget ?? HostileTarget;
	/// <summary>
	/// Icon only. This used to also accept ObjectHelper.IsBossFromTTK on the theory that "a boss
	/// outlives Config.BossTimeToKill, dungeon trash does not". A logged dungeon trace refutes
	/// that: all six trash pulls logged "boss detected" roughly twelve seconds in, then decayed
	/// back to no-boss once the estimate settled. GetTTK extrapolates from a five-sample HP-ratio
	/// moving average, so the first seconds of any pull - tiny HP delta over real elapsed time -
	/// project a TTK far past the 90s threshold. The test is not wrong about bosses, it is
	/// unusable early in combat, which is exactly when this classifier runs.
	///
	/// Failing that way is worse than the miss it was added to cover: it unlocked Wildfire on
	/// every trash pull and left it cooling down into the boss. Icon-only also puts this back in
	/// agreement with WildfireBossGuard below, which never consulted TTK. The combat-duration
	/// heuristic remains the fallback for a genuine icon miss.
	/// </summary>
	private bool IsCurrentTargetBoss => SchedulerTarget?.IsBossFromIcon() == true;
	private bool TargetDying => SchedulerTarget?.IsDying() == true;
	private bool ShouldOverdriveQueen
	{
		get
		{
			var target = SchedulerTarget;
			var ttk = target?.GetTTK() ?? float.NaN;
			// SummonTime already has the current GCD remainder subtracted out, which makes it a
			// weave-scheduling value, not a lifetime. Comparing a wall-clock TTK or downtime
			// against it understates how long the robot really has left and skips Overdrives a
			// Queen genuinely needs. Use the raw remaining summon time for both comparisons.
			var summonRemaining = MathF.Max(0f, SummonTimeRemainingRaw);
			// A Queen whose target dies rarely finishes on the next one: 6 of 21 crystal-phase Queens
			// in logs landed Crowned Collider despite other crystals being up, so an imminent death
			// Overdrives regardless of QueenHasNextTarget.
			var queenOutlivesTarget = ((TargetDying
				|| (!float.IsNaN(ttk) && ttk <= summonRemaining)) && !QueenHasNextTarget)
				|| MCHSyncPlusPolicy.QueenTargetEndsSoon(RecentObjectiveIn, summonRemaining);
			var queenOutlivesUptime = TimelineAvailable
				&& DowntimeWithin(summonRemaining);
			return MCHSyncPlusPolicy.ShouldOverdriveQueen(
				IsRobotActive, queenOutlivesTarget, queenOutlivesUptime);
		}
	}
	private bool TargetEndsBeforeWildfirePayload
	{
		get
		{
			var ttk = ObjectiveIn;
			if (!float.IsNaN(ttk) && ttk < WildfirePayloadWindow) return true;
			// GetTTK averages the target's whole history and lags ~2s; a crystal burned at ~100k/s read
			// 10.3s for a 6.9s death and its Wildfire detonated on nothing.
			var recent = RecentObjectiveIn;
			return !float.IsNaN(recent) && recent < MCHSyncPlusPolicy.RecentTTKHoldBelow;
		}
	}

	/// <summary>ObjectiveIn from the last few seconds of HP only; NaN without a usable sample.</summary>
	private static float RecentObjectiveIn => RecentObjectiveInOf(SchedulerTarget);

	private static float RecentObjectiveInOf(IBattleChara? target)
	{
		if (target == null || !TryRecentHp(target, out var then, out var seconds)) return float.NaN;
		return MCHSyncPlusPolicy.ObjectiveIn(target.GetHealthRatio(),
			MCHSyncPlusPolicy.RecentTTK(then, target.GetHealthRatio(), seconds), MCHSyncPlusPolicy.PhaseObjective(target.BaseId));
	}

	/// <summary>Oldest recorded HP ratio of the target from the last few seconds.</summary>
	private static bool TryRecentHp(IBattleChara target, out float ratio, out float seconds)
	{
		foreach (var (time, hps) in DataCenter.RecordedHP) // oldest first
		{
			seconds = (float)(DateTime.Now - time).TotalSeconds;
			if (seconds <= MCHSyncPlusPolicy.RecentTTKSpan && hps != null && hps.TryGetValue(target.GameObjectId, out ratio))
				return true;
		}
		ratio = seconds = 0f;
		return false;
	}

	/// <summary>
	/// Seconds until damage on the current target stops being progress: its death, or for a timed
	/// HP check the moment it is under the check. Same extrapolation as GetTTK, cut at the goal.
	/// </summary>
	private static float ObjectiveIn
	{
		get
		{
			var target = SchedulerTarget;
			if (target == null) return float.NaN;
			return MCHSyncPlusPolicy.ObjectiveIn(target.GetHealthRatio(), target.GetTTK(),
				MCHSyncPlusPolicy.PhaseObjective(target.BaseId));
		}
	}

	/// <summary>Another phase follows this target and the gauges carry into it.</summary>
	private static bool ObjectiveCarries => SchedulerTarget is { } t && MCHSyncPlusPolicy.PhaseObjective(t.BaseId) != null;

	/// <summary>
	/// What a human does at the end of an ultimate phase: once the push is secured (or about to be),
	/// stop feeding the boss and walk the gauges and two-minute cooldowns into the next phase.
	/// </summary>
	private protected static bool ObjectiveBanked => ObjectiveCarries && ObjectiveIn is >= 0f and < WildfirePayloadWindow;

	/// <summary>Seconds of damage left on this target: the phase leaving or the target dying.</summary>
	private float TargetTimeLeft
		=> Math.Min(DowntimeIn, SchedulerTarget?.GetTTK() is float ttk && !float.IsNaN(ttk) ? ttk : float.MaxValue);

	/// <summary>
	/// When the next damage window opens. BossMod knows the in-phase ones; a kill-pushed phase falls
	/// back to the measured FRU transition (P1->P2 4.7s, P3->P4 ~6s).
	/// </summary>
	private float NextUptimeIn
		=> TimelineEnabled && BMRDowntimeEndIn is > 0f and < float.MaxValue ? BMRDowntimeEndIn : TargetTimeLeft + 6f;

	/// <summary>
	/// Past a timed HP check the damage is worthless. A boss about to die still needs every instant
	/// hit (top logs fire Air Anchor, Drill, Chain Saw and Full Metal Field into the P4 kill).
	/// </summary>
	private static bool BankingTools => ObjectiveCarries && ObjectiveIn == 0f;

	/// <summary>A cooldown spent on worthless damage is only free if it is back by the next phase.</summary>
	protected override bool ShouldHoldTool(IBaseAction tool)
		=> BankingTools && MCHSyncPlusPolicy.ToolRefillAfterUse(
			tool.Cooldown.IsCoolingDown ? tool.Cooldown.RecastTimeRemain : 0f, tool.Cooldown.RecastTimeOneChargeRaw) > NextUptimeIn;

	private bool HasPartyBurstSources
	{
		get
		{
			UpdateExpectedPartyBuffs();
			return _expectedPartyBuffsCount > 0;
		}
	}
	// Trash vs boss tracking in dungeons: Automatic and Trust both. Trust still waits for live buffs
	// on the boss when the party has buff jobs; Automatic drops party sync in dungeons.
	private bool AutomaticDungeon => (SyncPlusBurstPlan is MCHSyncPlusPlan.Automatic or MCHSyncPlusPlan.TrustParty)
		&& DataCenter.Territory?.ContentType == RotationSolver.Basic.Data.TerritoryContentType.Dungeons;
	private bool SynchronizePartyBurst => SyncPlusBurstPlan != MCHSyncPlusPlan.Personal
		&& !(AutomaticDungeon && SyncPlusBurstPlan == MCHSyncPlusPlan.Automatic) && HasPartyBurstSources;
	protected override bool TimelineEnabled => UseEncounterTimeline;
	private protected bool TimelineAvailable => TimelineEnabled && BMRActive;
	// "The opener Queen has not gone out yet", with no clock and no last-GCD dependency. Both
	// earlier forms were too narrow to ever fire: CombatTime < 15f missed because Excavator lands
	// at ~18s behind the opener Hypercharge, and gating on IsLastGCD(Excavator) gave a single-GCD
	// window that the opener's own weave slots (Wildfire, Hypercharge, Reassemble, Double Check,
	// Checkmate) were already full of - a Lindwurm pull held qtarget:60 for 2.4s, found no slot, and snapped
	// back to 100. Battery only reaches 60 the first time during the opener, so this needs no
	// other qualifier, and it stays true until the Queen actually deploys.
	protected override bool QueenOpenerRelease => !_queenDeployedThisFight && Battery >= 60;
	private float? QueenPartyBurstPhase
	{
		get
		{
			if (_lastObservedPartyBurstAt < 0f) return null;

			var elapsed = MathF.Max(0f, EncounterTime - _lastObservedPartyBurstAt);
			return elapsed % PartyBurstCycle;
		}
	}
	private int QueenPartyBurstCycle
	{
		get
		{
			if (_lastObservedPartyBurstAt < 0f) return 0;

			var elapsed = MathF.Max(0f, EncounterTime - _lastObservedPartyBurstAt);
			return _observedPartyBurstCycle + (int)MathF.Floor(elapsed / PartyBurstCycle);
		}
	}
	// Passes SynchronizePartyBurst, not "an anchor has been learned". Before the first significant
	// party burst is observed the phase is null, and QueenBatteryTarget answers that case with the
	// cycle-1 target of 90. Gating on the anchor instead sent a synced fight down the personal
	// !synchronizePartyBurst branch and held for 100, which blocks the 1-minute 90 Queen in any
	// party whose buffs have not passed the significance test yet.
	private int QueenBatteryTarget => MCHSyncPlusPolicy.QueenBatteryTarget(
		SynchronizePartyBurst,
		SignificantPartyBurstActive,
		QueenOpenerRelease,
		QueenPartyBurstPhase,
		QueenPartyBurstCycle);

	/// <summary>
	/// Whether downtime prediction is actually answering AND near enough to matter right now, as
	/// opposed to merely being installed. BMRActive only reports that a boss module is loaded: a
	/// module whose timeline endpoints are missing leaves every downtime helper returning false
	/// forever, and switching the scheduler off leaves BMRActive true - either would silently
	/// disable the target-life fallback with nothing to replace it. Requiring proximity as well
	/// fixes a second gap: a downtime anywhere in the fight, even 120s out, used to count as
	/// "known" and waive the TTK check on a target that has nothing to do with that transition -
	/// most visibly on an add pull while the timeline describes the boss.
	/// </summary>
	private bool DowntimeGuardActive => UseEncounterTimeline
		&& BMRActive
		&& DowntimeWithin(WildfirePayloadWindow + LookAheadSeconds);

	private bool ProtectNextDungeonBossBurst => AutomaticDungeon
		&& _dungeonBossCompleted
		&& !_dungeonBossEncounter
		&& !IsCurrentTargetBoss
		&& WildfirePvE.EnoughLevel
		&& WildfirePvE.Cooldown.WillHaveOneCharge(30f);

	private protected bool ReserveBurst
	{
		get
		{
			UpdateDungeonTracker();
			return MCHSyncPlusPolicy.ShouldReserve(
				IsBurst,
				AutomaticDungeon && _bankDungeonBurst,
				ProtectNextDungeonBossBurst,
				_dungeonBossEncounter || IsCurrentTargetBoss);
		}
	}

	/// <summary>
	/// Kept only as the fallback signal for when no encounter timeline is available. The old
	/// rotation treated this extrapolation as its primary scheduler.
	/// </summary>
	private protected virtual float? SecondsUntilExpectedPartyBurst
	{
		get
		{
			if (_lastObservedPartyBurstAt < 0f) return null;

			var hold = MaximumWildfireHoldSeconds;
			var next = _lastObservedPartyBurstAt + PartyBurstCycle;
			while (next < EncounterTime - hold)
			{
				next += PartyBurstCycle;
			}
			return next - EncounterTime;
		}
	}

	/// <summary>
	/// One scheduling pass per frame, shared by every override below. The old rotation
	/// recomputed its burst state from inside six different property getters.
	/// </summary>
	private MCHSyncPlusDecision Decision
	{
		get
		{
			var now = EncounterTime;
			// CombatTimeRaw is a literal 0 for the entire out-of-combat period, so time alone
			// freezes the cache between fights - InCombat is what still moves there.
			var inCombat = InCombat;
			if (now == _decisionComputedAt && inCombat == _decisionComputedInCombat) return _decision;

			// Stamped BEFORE the trackers run, not after. The trackers read gates that route back
			// through this getter (ShouldCommitPersonalBurst, ShouldHoldFlexibleResources), so an
			// unstamped cache here recurses until the stack dies - which kills the game outright,
			// with no catchable exception and no CLR dump. A re-entrant read now gets last frame's
			// _decision, which is what UpdateBurstTracker already assumes it is reading anyway.
			_decisionComputedAt = now;
			_decisionComputedInCombat = inCombat;
			UpdateBurstTracker();
			UpdateDungeonTracker();
			// UpdateBurstTracker just ran above and may have logged a Wildfire use with whatever
			// reason was pending from the LAST frame Wildfire was committable - not this one. Do not
			// stash this frame's reason until after that read, or the fight report would attribute a
			// use to the reason computed on the frame the status became observable, which can already
			// be a different one (a Hold, even) if conditions moved on in the meantime. That
			// mismatch is exactly what the live pull's "released because: waiting for the rest" line
			// was: the Hold state present when the status finally appeared, not the Commit that
			// actually fired it a frame or more earlier.
			_decision = PlannedDecision() ?? MCHSyncPlusScheduler.Evaluate(new MCHSyncPlusSnapshot(
				IsBurst,
				ReserveBurst,
				// Trust party buffs with buff jobs present never takes the personal-burst Commit, even when no
				// buff was observed in the first 30s (HasPartyBurstSources then reads 0): Wildfire/Barrel wait
				// for live buffs. Scheduler only; Queen/Reassemble keep reading SynchronizePartyBurst.
				SynchronizePartyBurst || SyncPlusBurstPlan == MCHSyncPlusPlan.TrustParty && Buffs.Count > 0,
				SyncPlusBurstPlan == MCHSyncPlusPlan.TrustParty,
				WildfirePvE.Cooldown.HasOneCharge,
				WildfirePvE.Cooldown.WillHaveOneCharge(LookAheadSeconds),
				WildfireHeldFor,
				TimelineAvailable,
				DowntimeIn,
				TimelineEnabled ? BMRVulnerableIn : float.MaxValue,
				TimelineEnabled ? BMRVulnerableEndIn : float.MaxValue,
				PartyBurstActive,
				FullPartyBurstActive,
				SecondsInPartyBurst,
				SecondsUntilExpectedPartyBurst,
				WildfirePayloadWindow,
				MaximumWildfireHoldSeconds,
				// Adds die before missing buffs arrive: on the crystals a 5s wait for 2 more
				// buffs let the crystal fall too low, and Wildfire slid onto the Ice Veil and ghosted.
				// Top logs commit within ~1s of the buffs there.
				OverlapWait,
				LookAheadSeconds,
				ObjectiveBanked));

			if (_decision.Wildfire == MCHSyncPlusBurstGate.Commit && WildfirePvE.Cooldown.HasOneCharge)
			{
				_pendingWildfireReason = _decision.Reason;
				_pendingWildfireActiveBuffs = _activePartyBuffs;
				_pendingWildfireExpectedBuffs = _expectedPartyBuffsCount;
			}

			var reasonKey = MCHSyncPlusPolicy.StableDecisionReason(_decision.Reason);
			if (LogBurstReport && reasonKey != _lastReportedReasonKey && InCombat)
			{
				_lastReportedReasonKey = reasonKey;
				Svc.Log.Info($"[MCH Sync] {now:F1}s {_decision.Wildfire}/{_decision.Resources}: {_decision.Reason} (buffs {_activePartyBuffs}/{_expectedPartyBuffsCount}, Heat {Heat}, Battery {Battery})");
			}

			// After _decision is assigned, not inside UpdateBurstTracker: the gates this reads route
			// back through this getter, and from here they hit the stamped cache and report the plan
			// that actually belongs with the gauges on the same line. Called from the tracker it
			// paired this frame's gauges with the previous frame's plan.
			LogRotationState();

			return _decision;
		}
	}

	#region Burst hooks
	/// <summary>A burst decision from an authored plan; null leaves it to the scheduler. See MCH_Plan.</summary>
	private protected virtual MCHSyncPlusDecision? PlannedDecision() => null;

	/// <summary>Planned potion timing; null keeps the timed second/third/fourth potion settings.</summary>
	private protected virtual bool? PlannedPotionNow => null;

	/// <summary>
	/// Only true on an actual Commit. Free (Auto Burst off) used to read as "not Hold", which
	/// enabled Barrel Stabilizer and aggressive charge spending even with burst fully disabled;
	/// Hold already correctly returned false here, so tightening this to Commit-only fixes Free
	/// without touching the Hold behavior that was already right.
	/// </summary>
	protected override bool ShouldCommitPersonalBurst => Decision.Wildfire == MCHSyncPlusBurstGate.Commit;

	/// <summary>
	/// True when the Boss-only Wildfire restriction allows the current target. This lived only at
	/// the four WildfirePvE.CanUse sites, so every gate derived from ShouldUseWildfire believed a
	/// Wildfire was pending that the call sites would always refuse - which deadlocked Hypercharge
	/// behind PendingWildfireAroundFullMetal and stranded Heat at cap on any non-boss target.
	/// </summary>
	private bool WildfireTargetAllowed
		=> !WildfireBoss || SchedulerTarget?.IsBossFromIcon() == true;

	/// <summary>
	/// The base rotation passes skipTTKCheck on Wildfire. Always retain a payload-length target-life
	/// guard: a loaded BossMod timeline can describe the encounter correctly while the current
	/// target still dies before Wildfire detonates, as a logged zero-damage terminal use did.
	/// </summary>
	protected override bool ShouldUseWildfire
	{
		get
		{
			UpdateDungeonTracker();
			return MCHSyncPlusPolicy.ShouldUseWildfire(
				Decision.Wildfire == MCHSyncPlusBurstGate.Commit,
				AutomaticDungeon,
				_dungeonBossEncounter || IsCurrentTargetBoss,
				// split-HP P4 bosses read 'dying' while the TTK extrapolation still says >10s (logs: a 0-damage Wildfire)
				TargetEndsBeforeWildfirePayload || TargetDying,
				WildfireTargetAllowed);
		}
	}

	/// <summary>
	/// Split out from the Wildfire gate. Holding burst and banking resources are different
	/// decisions: ahead of downtime the rotation wants to withhold Wildfire while dumping Heat,
	/// which the single-state design in MCH Sync could not express.
	/// </summary>
	protected override bool ShouldHoldFlexibleResources => Decision.Resources == MCHSyncPlusResourcePlan.Bank;

	protected override bool ShouldSpendRemainingReassemble => (TargetDying && !ObjectiveCarries)
		|| Decision.Downtime == MCHSyncPlusDowntimeAction.Dump
		|| MCHSyncPlusPolicy.ShouldSpendReassembleInPartyBurst(
			PartyBurstActive, Decision.Resources == MCHSyncPlusResourcePlan.Spend);

	/// <summary>
	/// Lands Reassemble inside a raid-buff window rather than on whatever tool GCD happens to be
	/// next when it comes off cooldown - the same "align, don't just spend" idea already applied
	/// to Wildfire and Queen. Bounded the same way SecondsUntilExpectedPartyBurst already is: the
	/// prediction itself resets once it falls MaximumWildfireHoldSeconds behind, so this can never
	/// hold indefinitely. The projected max-charges check makes that full window safe: a charge is
	/// only delayed when it will not overcap before the predicted burst.
	/// </summary>
	protected override bool ShouldHoldReassembleForBuffs
		=> MCHSyncPlusPolicy.ShouldHoldReassembleForBuffs(
			SynchronizePartyBurst,
			PartyBurstActive,
			ReassembleWouldCapBeforeBurst,
			SecondsUntilExpectedPartyBurst,
			MaximumWildfireHoldSeconds);

	/// <summary>
	/// Reassemble has two charges, so "not capped right now" is not enough to make a hold free -
	/// the second charge can finish recharging during the hold and overcap before the window it is
	/// being saved for even opens. Checks the cap at the moment the hold would end, not just now.
	/// </summary>
	private bool ReassembleWouldCapBeforeBurst
	{
		get
		{
			var cooldown = ReassemblePvE.Cooldown;
			if (cooldown.CurrentCharges >= cooldown.MaxCharges) return true;
			return SecondsUntilExpectedPartyBurst is > 0f and float predicted
				&& cooldown.WillHaveXCharges(cooldown.MaxCharges, predicted);
		}
	}

	/// <summary>
	/// The Queen keeps attacking whatever is targeted next, so one target dying does not end her run
	/// while others remain. Logs: summoned on a crystal and overdriven 2.5s later with the Ice Veil
	/// still up, throwing away 50 Battery.
	/// </summary>
	// A second enemy that is dying too (the P4 pair) is not somewhere for the Queen to go.
	private bool QueenHasNextTarget => AllHostileTargets.Any(t => t != SchedulerTarget && !t.IsDying());

	/// <summary>Same target-life guard as Wildfire, unless another enemy is left to take the Hypercharge (M3 logs: Barrel at the kill).</summary>
	protected override bool ShouldHoldBarrel => (TargetEndsBeforeWildfirePayload || TargetDying) && !QueenHasNextTarget;

	/// <summary>
	/// FRU P3/P4: Barrel goes on cooldown ahead of the buffs so it is back for the CT downtime Barrel (top MCH logs: P3 ~8:10,
	/// P4 ~10:10-10:22, CT 12:10-12:22, ten Barrels to nine Wildfires; buff-gated it came back 12:43, after the CT return).
	/// Its Full Metal Field and Hypercharged still wait for a burst within their 30s, else go as an unclaimed Barrel.
	/// Wildfire still waits for live buffs.
	/// </summary>
	protected override bool ShouldUseBarrelOnCooldown => IsBurst && DataCenter.IsInFRU
		&& SchedulerTarget is { } target && MCHSyncPlusPolicy.BarrelOnCooldownPhase(target.BaseId)
		&& !ObjectiveBanked && !DowntimeWithin(WildfirePayloadWindow);

	private float RequiredQueenLifetime => Math.Max(QueenExecutionWindow, AutomatonQueenPvE.EnoughLevel
		? AutomatonQueenPvE.Config.TimeToKill : RookAutoturretPvE.Config.TimeToKill);
	// Battery carries into the next phase, so a phase end is never a "last chance" Queen.
	private bool LastViableQueenWindow => (!AutomaticDungeon || _dungeonBossEncounter) && !ObjectiveCarries
		&& MCHSyncPlusPolicy.ShouldReleaseFinalQueen(
		Battery, IsRobotActive, SchedulerTarget?.GetTTK() ?? float.NaN,
		RequiredQueenLifetime,
		GCDTime(2) + 1f, TimelineAvailable ? DowntimeIn : float.MaxValue);

	protected override bool ShouldHardHoldQueen(int projectedBatteryGain)
		=> MCHSyncPlusPolicy.ShouldHoldQueenForLifetime(QueenHasNextTarget ? float.NaN : ObjectiveIn, RequiredQueenLifetime)
		|| (!LastViableQueenWindow && (MCHSyncPlusPolicy.ShouldHardHoldQueen(
			TimelineAvailable,
			DowntimeWithin(RequiredQueenLifetime))
		// Not gated on a learned anchor any more. QueenBatteryTarget always returns a target,
		// and in personal mode it is now 100 - but the base rotation's generic "battery >= 90"
		// deploy rule fires before that, so without a hold here the personal target could never be
		// reached. ShouldHoldQueenForBatteryTarget still stands down before an actual overcap.
		|| MCHSyncPlusPolicy.ShouldHoldQueenForBatteryTarget(
			Battery, QueenBatteryTarget, projectedBatteryGain)));

	protected override bool ShouldHoldQueen
	{
		get
		{
			// The soft scheduling hold never banks a capped gauge. The hard lifetime/downtime
			// guard in UseQueen still wins when a summon cannot finish its damage.
			if (Battery >= 100 || LastViableQueenWindow) return false;

			// No Battery test needed - the capped case already returned above.
			if (!DowntimeGuardActive && TargetDying) return true;

			// Deliberately NOT "if (ReserveBurst) return true;" any more. ReserveBurst conflates two
			// unrelated states - "Auto Burst is off" and "banking for the next dungeon boss" - and
			// the two consumers of it disagreed. Evaluate tests !AutoBurst FIRST and answers
			// Free/SPEND, so the scheduler had already decided burst-off must not bank; reading
			// ReserveBurst directly here overrode that and held the gauge anyway. That is what
			// pinned Battery at 100 for 48s in one pull and lost 50 to an Excavator, a Clean Shot and
			// an Air Anchor. The genuine boss reserve still holds, because Evaluate answers it as
			// Hold/BANK and the line below reads exactly that.
			return Decision.Resources == MCHSyncPlusResourcePlan.Bank;
		}
	}
	protected override bool ShouldReleaseQueenAtMinimumBattery
		=> LastViableQueenWindow || MCHSyncPlusPolicy.ShouldReleaseQueenAtBatteryTarget(Battery, QueenBatteryTarget);

	protected override bool ShouldHoldExcavatorForBattery
		=> BankingTools || MCHSyncPlusPolicy.ShouldHoldExcavatorForBattery(
			PartyBurstActive, HasFullMetalMachinist, Battery, QueenBatteryTarget);

	/// <summary>
	/// Fires when Queen is SELECTED, not when it is cast - the base rotation calls this as soon as
	/// TryDeployQueen's CanUse recommends the action, and the framework may still not cast it that
	/// frame. A Vamp Fatale pull logged ten of these against nine real casts. Nothing here
	/// may be treated as confirmation; see the robot observation in UpdateBurstTracker.
	///
	/// What it logs is the timeline state the selection saw, so a Queen that gets cut short is
	/// diagnosable after the fact instead of guessed at: whether a BossMod module was active for
	/// this encounter at all, and what it did or did not know about the next downtime/vulnerability
	/// transition at that moment.
	/// </summary>
	protected override void OnQueenDeployed(int battery)
		=> LogQueenTimelineState($"selected at {battery} Battery");

	/// <summary>
	/// True in the early portion of the GCD right after Full Metal Field resolves, before the
	/// late-weave slot where MCH_Reborn's own Wildfire-around-FMF check gets to run. Confirmed live:
	/// the opener went Full Metal Field -> Hypercharge -> five Blazing Shots with no Wildfire at
	/// all. MCH_Reborn's flexible-Hypercharge fallback has no idea a Wildfire weave is waiting for
	/// its slot later in this same GCD, so it fires as soon as its own conditions are met - which,
	/// right after Full Metal Field, is almost immediately.
	///
	/// Checks both sides of the Full Metal Field transition on purpose. HasFullMetalMachinist is
	/// the buff that grants Full Metal Field, and Full Metal Field consumes it the moment it
	/// executes - ShouldRecoverLostFullMetalBurst's own !HasFullMetalMachinist requirement is the
	/// proof. So the very state this guard exists to hold - the frames right after Full Metal
	/// Field fires, exactly where the live pull's Hypercharge won the race - is precisely where
	/// HasFullMetalMachinist alone reads false. IsLastGCD(FullMetalFieldPvE) covers that half: it
	/// stays true for the rest of this GCD regardless of the buff. Because EmergencyAbility runs
	/// before AttackAbility, the hold must remain through the legal weave and end only after
	/// Wildfire actually consumes its cooldown.
	///
	/// Requires Wildfire to be genuinely castable, not merely off cooldown. Holding every
	/// Hypercharge path for a weave that range or target checks will refuse strands Heat at cap
	/// until Full Metal Machinist expires on its own, once per Barrel Stabilizer cycle.
	/// The payload must also be executable; disabled Full Metal Field or Hypercharge cannot
	/// reserve a weave. Their native CanUse checks do not call the rotation scheduling gates.
	/// </summary>
	private bool PendingWildfireAroundFullMetal
		=> MCHSyncPlusPolicy.ShouldHoldHyperchargeForFullMetalWildfire(
			HasFullMetalMachinist || IsLastGCD(false, FullMetalFieldPvE),
			ShouldUseWildfire,
			WildfirePvE.Cooldown.HasOneCharge && WildfirePvE.CanUse(out _, skipTTKCheck: true),
			CanStartWildfirePayload);

	private bool CanStartWildfirePayload
		=> HyperchargePvE.CanUse(out _, skipTTKCheck: true)
			&& (!HasFullMetalMachinist || FullMetalFieldPvE.CanUse(out _, skipTTKCheck: true));

	private bool PostFullMetalWildfirePayloadPending
		=> MCHBurstActionPolicy.ShouldStartWildfireHypercharge(
			HasWildfire,
			HasFullMetalMachinist)
		&& !IsOverheated;

	private bool DelayFullMetalForLeadingWildfire
		=> WildfireWeave == MCHSyncPlusWildfireWeave.EveryBurst
			&& MCHSyncPlusPolicy.ShouldDelayFullMetalForLeadingWildfire(
				ShouldUseWildfire,
				WildfirePvE.Cooldown.HasOneCharge,
				HasFullMetalMachinist,
				CanStartWildfirePayload,
				DowntimeWithin(WildfirePayloadWindow));

	/// <summary>
	/// MCH Sync blocks every Hypercharge while Full Metal Machinist is held, and ToolChargeSoon
	/// early-returns on that, which defeats the Heat == 100 overcap escape in the base rotation.
	/// Heat cap now always wins; imminent downtime outranks it until the scheduler's hold ceiling.
	/// PendingWildfireAroundFullMetal
	/// outranks everything here, including the Heat cap escape: every Hypercharge path in
	/// MCH_Reborn.EmergencyAbility nests inside this one property, so it is the only choke point
	/// that can hold all of them off long enough for the Wildfire weave to get its legal slot.
	/// </summary>
	protected override bool ShouldUseHypercharge
		=> MCHSyncPlusPolicy.ShouldUseHypercharge(
			// Heat banks only once the check is met (BankingTools), not when it is predicted soon: top FRU MCH logs
			// Hypercharge 1-9s before Usurper/Oracle cross 20% (5/8) and into the P4 kill (4/8). Banked Heat is only
			// dumped when sitting capped would waste more than the 50 it costs.
			ObjectiveCarries ? BankingTools && !(Heat == 100 && TargetTimeLeft > 30f) : TargetDying,
			HasWildfire,
			DowntimeWithin(GCDTime(3)),
			Decision.Downtime == MCHSyncPlusDowntimeAction.HoldCeiling,
			HasFullMetalMachinist,
			Heat == 100,
			WildfirePayloadWindowClosing,
			ToolChargeWithinRestWindow)
		&& !PendingWildfireAroundFullMetal
		&& !BarrelHandoffPending;

	/// <summary>
	/// Barrel Stabilizer is about to open a committed burst: the capped-Heat Hypercharge (an emergency, so it ran
	/// before Barrel's attack slot) waits one weave for it. 7 of 172 logged Barrels came right after such a
	/// Hypercharge, and Wildfire then landed 8.2s after Barrel instead of 3.5s. Same gate as the Barrel cast.
	/// </summary>
	private bool BarrelHandoffPending
		=> !HasWildfire && !HasFullMetalMachinist && ShouldCommitPersonalBurst
			&& !DowntimeWithin(GCDTime(2)) && !ShouldHoldBarrel && BarrelStabilizerPvE.CanUse(out _);

	/// <summary>
	/// True once the live Wildfire has less time left than its Hypercharge dump needs, which is
	/// the point past which holding Hypercharge for Full Metal Field costs a Blazing Shot per GCD.
	/// </summary>
	private bool WildfirePayloadWindowClosing
	{
		get
		{
			// Both target-side ids and the same SchedulerTarget as WildfireTargetEnding, so this
			// reads the carrier the rotation actually aimed at.
			var remain = SchedulerTarget?.StatusTime(true, StatusID.Wildfire, StatusID.Wildfire_1323) ?? 0f;
			// A zero reading means the carrier or the status id was not found, not that Wildfire
			// is about to expire. Treating that as "closing" would fail open and surrender the
			// Full Metal Field protection on every pull the timer cannot be read on, so an
			// unreadable timer holds exactly as it did before.
			return remain > 0f && remain < HyperchargePayloadWindow;
		}
	}

	/// <summary>
	/// Preserve Full Metal Field until an imminent Wildfire is actually ready to pair with it.
	/// The base rotation's expiring-buff safeguard remains ungated, so this cannot lose the use.
	/// </summary>
	protected override bool ShouldUseFullMetalField
	{
		get
		{
			// The expiring-buff path in GeneralGCD still spends it before it is lost.
			if (BankingTools) return false;
			if (UnclaimedBarrelBuffs) return true;
			if (WildfireWeave == MCHSyncPlusWildfireWeave.AfterFullMetal) return true;

			if (DelayFullMetalForLeadingWildfire)
			{
				return false;
			}

			return !MCHSyncPlusPolicy.ShouldHoldFullMetalForWildfire(
				IsBurst,
				WildfirePvE.Cooldown.WillHaveOneCharge(LookAheadSeconds),
				WildfirePvE.Cooldown.HasOneCharge,
				Decision.Wildfire == MCHSyncPlusBurstGate.Commit,
				DowntimeWithin(WildfirePayloadWindow));
		}
	}
	#endregion

	#region Action overrides
	/// <summary>
	/// Withholding the GCD here makes RotationSolver press nothing at all, so only do it while
	/// Hypercharge is genuinely available, and bail out if the wait ever runs long.
	/// </summary>
	protected override bool GeneralGCD(out IAction? act)
	{
		if (PaddingFatebreaker)
		{
			act = null;
			return false;
		}

		// A downtime Barrel's Full Metal Field opens the uptime (top FRU logs: first GCD back, 3/3 kills).
		if (UnclaimedBarrelBuffs && HasFullMetalMachinist && FullMetalFieldPvE.CanUse(out act, skipTTKCheck: true))
		{
			return true;
		}

		if (MCHSyncPlusPolicy.ShouldPrioritizeFullMetalAfterWildfire(
			HasFullMetalMachinist,
			HasWildfire,
			IsLastAction(false, WildfirePvE))
			&& FullMetalFieldPvE.CanUse(out act, skipTTKCheck: true))
		{
			return true;
		}

		var hyperchargeChargeReady = HyperchargePvE.IsEnabled
			&& HyperchargePvE.EnoughLevel
			&& HyperchargePvE.Cooldown.HasOneCharge;
		var gcdQueueReady = WeaponRemain <= DataCenter.CalculatedActionAhead;
		var payloadPending = PostFullMetalWildfirePayloadPending;

		// The watchdog measures one handoff, so it may only be cleared when that handoff is over.
		// Clearing it whenever the GCD rolled over made MaximumHyperchargeStall a fresh budget per
		// GCD instead of a ceiling on the wait: a Hypercharge that is charged but not castable -
		// out of range mid-Wildfire - re-armed the stall on every GCD for the whole window.
		if (!payloadPending)
		{
			_hyperchargeStallStartedAt = -1f;
		}

		if (MCHSyncPlusPolicy.ShouldWaitForWildfireHyperchargeGcd(
			payloadPending,
			hyperchargeChargeReady,
			gcdQueueReady,
			StallElapsed(),
			MaximumHyperchargeStall))
		{
			act = null;
			return false;
		}

		// The base rotation preserves a 1-2-3 combo before checking tool GCDs. That can turn an
		// Air Anchor which comes ready at the queue boundary into two filler GCDs of drift.
		if (MCHSyncPlusPolicy.ShouldPrioritizeAirAnchor(payloadPending, IsOverheated, SpreadShotPvE.CanUse(out _))
			&& !ShouldHoldTool(AirAnchorPvE) && AirAnchorPvE.CanUse(out act))
		{
			return true;
		}

		return base.GeneralGCD(out act);
	}

	private float StallElapsed()
	{
		var now = EncounterTime;
		if (_hyperchargeStallStartedAt < 0f || now < _hyperchargeStallStartedAt)
		{
			_hyperchargeStallStartedAt = now;
			return 0f;
		}
		return now - _hyperchargeStallStartedAt;
	}

	private float BarrelBuffsLeft
		=> StatusHelper.PlayerStatusTime(true, StatusID.FullMetalMachinist, StatusID.Hypercharged);

	/// <summary>Barrel's buffs with no burst able to claim them before they expire: the downtime Barrel.</summary>
	private bool UnclaimedBarrelBuffs
		=> (HasFullMetalMachinist || HasHypercharged) && !HasWildfire
			&& Decision.Wildfire != MCHSyncPlusBurstGate.Commit
			&& MCHSyncPlusPolicy.BarrelBuffsUnclaimed(BarrelBuffsLeft, SecondsUntilExpectedPartyBurst);

	/// <summary>
	/// Boss-gone actions from the top FRU logs (3/3 kills, P4 downtime ~11:35-12:26): Barrel ~15s before the boss
	/// returns when the next burst is a full Barrel cycle away (it is back for P5's opener, one extra Barrel per
	/// kill), and Queen ~4s before it so she is hitting on return. Wildfire stays buffs-only.
	/// AttackAbility never runs without a target in range, so these live here.
	/// </summary>
	protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
	{
		var uptimeIn = BMRDowntimeEndIn;
		if (InCombat && !HasHostilesInRange && TimelineAvailable)
		{
			if (IsBurst && MCHSyncPlusPolicy.ShouldUseDowntimeBarrel(uptimeIn, SecondsUntilExpectedPartyBurst)
				&& BarrelStabilizerPvE.CanUse(out act))
			{
				return true;
			}

			var battery = Battery;
			if (MCHSyncPlusPolicy.ShouldPreSummonQueen(uptimeIn, battery, IsRobotActive)
				&& !ShouldHoldQueen && !ShouldHardHoldQueen(0)
				&& AutomatonQueenPvE.CanUse(out act, skipTTKCheck: true))
			{
				OnQueenDeployed(battery);
				return true;
			}
		}

		return base.GeneralAbility(nextGCD, out act);
	}

	protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
	{
		// Hypercharge and Wildfire live here.
		if (PaddingFatebreaker)
		{
			act = null;
			return false;
		}

		// Through the Decision cache, not a bare UpdateBurstTracker() call. RotationSolver re-runs
		// EmergencyAbility several times per real frame for its next-action preview, so a direct
		// call ran the tracker several times per frame, outside the per-frame stamp that exists to
		// prevent exactly that. Every edge test in the tracker happens to be idempotent today, so
		// nothing was visibly broken - but the duplicate battery-loss lines carried the SAME
		// timestamp as their gain, which is same-frame re-entry, and the next non-idempotent
		// statement added there would fail silently. Reading Decision still guarantees the tracker
		// has run this frame, because the getter runs it on the first read.
		_ = Decision;
		// The downtime Barrel's free Hypercharge, after its Full Metal Field and around the tools (top logs: ~10s
		// into the uptime). Nothing else would release it: every Hypercharge path waits for a Wildfire not coming.
		if (UnclaimedBarrelBuffs && HasHypercharged && !HasFullMetalMachinist && !IsOverheated
			&& (!ToolChargeWithinRestWindow || BarrelBuffsLeft < GCDTime(3))
			&& HyperchargePvE.CanUse(out act, skipTTKCheck: true))
		{
			return true;
		}

		// Protect the two burst handoffs from a pet weave; terminal Detonator still wins below.
		if (!(HasWildfire && HasFullMetalMachinist) && !PostFullMetalWildfirePayloadPending
			&& ShouldOverdriveQueen
			&& (QueenOverdrivePvE.EnoughLevel
				? QueenOverdrivePvE.CanUse(out act, skipTTKCheck: true, targetOverride: TargetType.Self)
				: RookOverdrivePvE.CanUse(out act, skipTTKCheck: true, targetOverride: TargetType.Self)))
		{
			LogQueenTimelineState("overdrive");
			return true;
		}
		// Ahead of Hypercharge deliberately. Once the terminal predicate is true there may be a
		// single weave slot left, and starting a fresh payload with it banks damage that will never
		// detonate - whereas Detonator releases everything Wildfire has already accumulated.
		if (MCHSyncPlusPolicy.ShouldDetonateWildfire(
			HasWildfire,
			DetonatorPvEReady,
			WildfireTargetEnding,
			DowntimeWithin(1f))
			&& DetonatorPvE.CanUse(out act, skipTTKCheck: true))
		{
			return true;
		}

		if (PostFullMetalWildfirePayloadPending
			&& HyperchargePvE.CanUse(out act, skipTTKCheck: true))
		{
			return true;
		}

		// Give an overflowing Queen the weave before optional Hypercharge. Active or pending
		// Wildfire keeps its handoff; UseQueen still applies every lifetime and target guard.
		if (!HasWildfire && !PendingWildfireAroundFullMetal
			&& UseQueen(out act, nextGCD, onlyToAvoidOvercap: true))
		{
			return true;
		}

		if (MCHSyncPlusPolicy.ShouldUseOpenerMedicine(
			OpenerBurstMeds,
			InCombat,
			CombatTime,
			WildfirePvE.Cooldown.HasOneCharge,
			ShouldUseWildfire)
			&& UseBurstMedicine(out act))
		{
			return true;
		}
		if (PlannedPotionNow is bool planned)
		{
			if (planned && UseBurstMedicine(out act)) return true;
		}
		else
		{
			if (ShouldUseSecondMedicine && UseBurstMedicine(out act)) return true;
			if (ShouldUseThirdMedicine && UseBurstMedicine(out act)) return true;
			if (ShouldUseFourthMedicine && UseBurstMedicine(out act)) return true;
		}
		return base.EmergencyAbility(nextGCD, out act);
	}

	/// <summary>
	/// A live accepted party-buff window may lead with Wildfire while Full Metal Machinist is ready,
	/// even before Full Metal Field wins the base rotation's GCD selection. GeneralGCD then forces
	/// Full Metal Field immediately after Wildfire. The explicit after-FMF mode remains the opt-out.
	/// </summary>
	protected override bool AttackAbility(IAction nextGCD, out IAction? act)
	{
		if (PaddingFatebreaker)
		{
			act = null;
			return false;
		}

		if (MCHSyncPlusPolicy.ShouldWeaveWildfireBeforeFullMetal(
				WildfireWeave == MCHSyncPlusWildfireWeave.EveryBurst,
				ShouldUseWildfire,
				WildfirePvE.Cooldown.HasOneCharge,
				nextGCD.IsTheSameTo(false, FullMetalFieldPvE) || DelayFullMetalForLeadingWildfire,
				PartyBurstActive,
				HasFullMetalMachinist,
				CanStartWildfirePayload,
				CanLateWeave && WeaponRemain <= WildfireLateWeaveWindow,
				DowntimeWithin(WildfirePayloadWindow))
			&& WildfirePvE.CanUse(out act, skipTTKCheck: true)
			&& WildfireResolvedTargetAllowed)
		{
			return true;
		}

		return base.AttackAbility(nextGCD, out act);
	}

	private bool WildfireTargetEnding
	{
		get
		{
			// The enemy carrying our Wildfire, which is not the selected target after a switch.
			var target = AllHostileTargets.FirstOrDefault(t => t.HasStatus(true, StatusID.Wildfire, StatusID.Wildfire_1323))
				?? SchedulerTarget;
			// Both target-side ids, matching ObjectHelper's own MCH Wildfire targeting check. Only
			// StatusID.Wildfire was tested here, so if a pull applies the Wildfire_1323 variant this
			// reads false, WildfireTargetEnding never trips, and Detonator never fires on a dying
			// target - the payload expires unspent. Detonator has not fired in any logged pull, so
			// this path had no live evidence either way.
			if (target?.HasStatus(true, StatusID.Wildfire, StatusID.Wildfire_1323) != true) return false;
			if (target.GetTTK() <= 1f) return true;
			// GetTTK lags ~2s, so crystals died 0.1-0.9s before the detonation and it hit nothing
			// (8 of 49 logged crystal Wildfires). The recent rate sees the death in time.
			return MCHSyncPlusPolicy.ShouldDetonateEarly(RecentObjectiveInOf(target),
				target.StatusTime(true, StatusID.Wildfire, StatusID.Wildfire_1323));
		}
	}

	/// <summary>
	/// Gated on ShouldUseWildfire, not the raw scheduler label. Decision.Wildfire == Commit only
	/// means the scheduler sees no reason to keep waiting; it says nothing about a dying target, a
	/// dungeon boss not yet reached, or Wildfire being off-cooldown. ShouldUseWildfire already
	/// carries those checks, so reusing it stops the potion firing on a target that Wildfire itself
	/// would refuse.
	/// </summary>
	private bool ShouldUseSecondMedicine
		=> MCHSyncPlusPolicy.ShouldUseSecondMedicine(
			UseSecondBurstMedicine,
			InCombat,
			ReserveBurst,
			CombatTime,
			SecondMedicineTime,
			PartyBurstCycle,
			WildfirePvE.Cooldown.HasOneCharge,
			ShouldUseWildfire);

	private bool ShouldUseFourthMedicine
		=> MCHSyncPlusPolicy.ShouldUseSecondMedicine(
			UseFourthBurstMedicine,
			InCombat,
			ReserveBurst,
			CombatTime,
			FourthMedicineTime,
			PartyBurstCycle,
			WildfirePvE.Cooldown.HasOneCharge,
			ShouldUseWildfire);

	private bool ShouldUseThirdMedicine
		=> MCHSyncPlusPolicy.ShouldUseSecondMedicine(
			UseThirdBurstMedicine,
			InCombat,
			ReserveBurst,
			CombatTime,
			ThirdMedicineTime,
			PartyBurstCycle,
			WildfirePvE.Cooldown.HasOneCharge,
			ShouldUseWildfire);

	#endregion

	#region Trackers
	private void UpdateBurstTracker()
	{
		var encounterTime = EncounterTime;
		var elapsed = _lastEncounterTime >= 0f && encounterTime >= _lastEncounterTime
			? encounterTime - _lastEncounterTime
			: 0f;
		// A combat timer running backwards is the only reliable new-fight edge available here.
		if (_lastEncounterTime >= 0f && encounterTime < _lastEncounterTime)
		{
			ReportFight();

			_lastObservedPartyBurstAt = -1f;
			_observedPartyBurstCycle = 0;
			_partyBurstStartedAt = -1f;
			_anchorWorthyStartedAt = -1f;
			_wildfireReadyAt = -1f;
			_hyperchargeStallStartedAt = -1f;
			_lastQueenLogAt = float.MinValue;
			_lastLoggedBattery = -1;
			_lastCappedBatterySource = string.Empty;
			_queenDeployedThisFight = false;
			_robotWasActive = false;
			_lastRotationState = string.Empty;
			_partyBurstWasActive = false;
			_anchorWasWorthy = false;
			_wildfireWasReady = false;
			_hadWildfire = false;
			_interruptionStartedAt = -1f;
			_partyAnchorNeedsRecovery = false;
			_pendingWildfireReason = string.Empty;
			_pendingWildfireActiveBuffs = 0;
			_pendingWildfireExpectedBuffs = 0;
			_lastReportedReasonKey = string.Empty;
			_observedPartyBuffs.Clear();
		}

		_lastEncounterTime = encounterTime;
		UpdateExpectedPartyBuffs();
		if (!InCombat) return;

		// Death or ten seconds without an attackable target invalidates the old cadence.
		// Require a full observed overlap to relearn it; a lone recovery cooldown is insufficient.
		if (IsDead || SchedulerTarget?.IsTargetable != true)
		{
			if (_interruptionStartedAt < 0f) _interruptionStartedAt = encounterTime;
			if (IsDead || encounterTime - _interruptionStartedAt >= WildfirePayloadWindow)
				_partyAnchorNeedsRecovery = true;
		}
		else _interruptionStartedAt = -1f;

		// The robot being out is the only proof a Queen actually landed. Setting this from
		// OnQueenDeployed instead treated a mere recommendation as a cast: on a Vamp Fatale pull a
		// Queen was selected at 11.1s and never cast (Battery stayed 60, robot stayed 0), which
		// closed QueenOpenerRelease and stranded the opener Queen until the gauge hit 100 at 41.6s,
		// long after the opener buffs had gone.
		// The rising edge is the cast itself, and LastSummonBatteryPower is the gauge the game
		// actually consumed - not the value read at selection time, which is what "selected at N
		// Battery" reports and which may belong to a recommendation that was never cast. Logging
		// the confirmed number alongside it makes the two directly comparable, so the ten-selections
		// -against-nine-casts discrepancy can never again need FFLogs to detect.
		var robotActive = IsRobotActive;
		if (robotActive && !_robotWasActive)
		{
			_queenDeployedThisFight = true;
			if (LogBurstReport)
			{
				Svc.Log.Info(
					$"[MCH Sync] {encounterTime:F1}s Queen CONFIRMED cast at "
					+ $"{LastSummonBatteryPower} Battery");
			}
		}
		_robotWasActive = robotActive;

		LogBatteryChange();

		// This interval elapsed under the previously cached decision. Counting only Hold frames
		// avoids reporting every second that Wildfire was merely ready as scheduler-induced delay.
		if (_wildfireWasReady && _decision.Wildfire == MCHSyncPlusBurstGate.Hold)
		{
			_totalHeldSeconds += elapsed;
		}

		var partyBurstActive = PartyBurstActive;
		if (partyBurstActive && !_partyBurstWasActive)
		{
			_partyBurstStartedAt = encounterTime;
		}
		else if (!partyBurstActive)
		{
			_partyBurstStartedAt = -1f;
		}
		_partyBurstWasActive = partyBurstActive;

		// Re-anchor only near the learned 120-second cadence. This keeps a single off-cycle job
		// from moving the whole party clock while still accepting normal drift and missed cycles.
		//
		// The anchor timestamp is its own rising edge, not partyBurstStartedAt above. When AnchorJob
		// is set, the anchor job's own buff can go active well after some unrelated buff already
		// started the generic party-burst window - reusing that earlier timestamp would backdate
		// the anchor to a moment the anchor job had nothing to do with. Tracking IsAnchorWorthy's
		// own edge keeps the anchor tied to the condition that actually justified moving it, in
		// both AnchorJob and default (distinct-job) mode.
		//
		// Checked on every frame the window is worthy, not only its first, because raid buffs land
		// staggered: the opening frame is one buff of four and never looks anchor-worthy on its own.
		// The gap test stops this from re-anchoring repeatedly inside one already-recognized window.
		var anchorWorthy = !IsDead && SchedulerTarget?.IsTargetable == true && IsAnchorWorthy();
		if (anchorWorthy && !_anchorWasWorthy)
		{
			_anchorWorthyStartedAt = encounterTime;
		}
		else if (!anchorWorthy)
		{
			_anchorWorthyStartedAt = -1f;
		}
		_anchorWasWorthy = anchorWorthy;

		if (anchorWorthy
			&& MCHSyncPlusPolicy.ShouldObservePartyBurst(
				_lastObservedPartyBurstAt, _anchorWorthyStartedAt,
				PartyBurstCycle, _partyAnchorNeedsRecovery && FullPartyBurstActive))
		{
			_observedPartyBurstCycle = MCHSyncPlusPolicy.AdvancePartyBurstCycle(
				_observedPartyBurstCycle,
				_lastObservedPartyBurstAt,
				_anchorWorthyStartedAt,
				PartyBurstCycle);
			_lastObservedPartyBurstAt = _anchorWorthyStartedAt;
			_partyAnchorNeedsRecovery = false;
		}

		var wildfireReady = WildfirePvE.Cooldown.HasOneCharge;
		// Banked time is not a preference wait: the hold ceiling starts when the next phase does.
		if (wildfireReady && (!_wildfireWasReady || ObjectiveBanked))
		{
			_wildfireReadyAt = encounterTime;
		}
		else if (!wildfireReady)
		{
			_wildfireReadyAt = -1f;
		}
		_wildfireWasReady = wildfireReady;

		// Uses the reason pending from the last frame Wildfire was actually committable, not this
		// frame's freshly-recomputed one - see the note in the Decision getter above.
		var hasWildfire = HasWildfire;
		if (hasWildfire && !_hadWildfire)
		{
			_wildfireUses.Add(new MCHSyncPlusWildfireUse(
				encounterTime,
				_pendingWildfireActiveBuffs,
				_pendingWildfireExpectedBuffs,
				_pendingWildfireReason,
				TimelineEnabled && BMRVulnWithin(1f),
				DowntimeIn));
		}
		_hadWildfire = hasWildfire;
	}

	/// <summary>
	/// Turns "that felt wrong" into something checkable without opening a log parser.
	/// </summary>
	private void ReportFight()
	{
		if (!LogBurstReport || _wildfireUses.Count == 0)
		{
			_wildfireUses.Clear();
			_totalHeldSeconds = 0f;
			return;
		}

		var castWithFullOverlap = 0;
		var castWithPartialOverlap = 0;
		var castBeforeTrackedBuffs = 0;
		var inVuln = 0;
		var cutShort = 0;
		foreach (var use in _wildfireUses)
		{
			if (use.ExpectedBuffs > 0 && use.ActiveBuffs >= use.ExpectedBuffs) castWithFullOverlap++;
			else if (use.ActiveBuffs > 0) castWithPartialOverlap++;
			else castBeforeTrackedBuffs++;

			if (use.InVulnerabilityWindow) inVuln++;
			if (use.DowntimeIn > 0f && use.DowntimeIn < WildfirePayloadWindow) cutShort++;
		}

		Svc.Log.Info($"[MCH Sync] Fight report: {_wildfireUses.Count} Wildfire uses - cast state: {castWithFullOverlap} with full tracked overlap, {castWithPartialOverlap} with partial tracked overlap, {castBeforeTrackedBuffs} before tracked buffs; payload buff coverage is not inferred; {inVuln} cast in a vulnerability window; {cutShort} started too close to known downtime; {_totalHeldSeconds:F1}s scheduler Hold time while Wildfire was ready.");
		for (var index = 0; index < _wildfireUses.Count; index++)
		{
			var use = _wildfireUses[index];
			Svc.Log.Info($"[MCH Sync]   #{index + 1} at {use.At:F1}s - cast state: {MCHSyncPlusPolicy.DescribeWildfireCastState(use.ActiveBuffs, use.ExpectedBuffs)} ({use.ActiveBuffs}/{use.ExpectedBuffs}); payload coverage not inferred; released because: {use.Reason}");
		}

		_wildfireUses.Clear();
		_totalHeldSeconds = 0f;
	}

	private void UpdateExpectedPartyBuffs()
	{
		StatusList();
		var encounterTime = EncounterTime;
		var buffsVersion = PartyBuffsVersion;
		if (encounterTime == _lastPartyBuffScanAt
			&& InCombat == _lastPartyBuffScanInCombat
			&& buffsVersion == _lastPartyBuffScanVersion)
		{
			return;
		}

		_lastPartyBuffScanAt = encounterTime;
		_lastPartyBuffScanInCombat = InCombat;
		_lastPartyBuffScanVersion = buffsVersion;
		var anchorJob = AnchorJob == MCHSyncPlusAnchorJob.Automatic ? null : AnchorJob.ToString();
		var anchorJobActive = false;
		var active = 0;
		var expected = 0;
		var ninjaActive = false;
		var ninjaExpected = false;
		var shortestActiveRemaining = float.MaxValue;
		var names = new List<string>(Buffs.Count);
		var activeJobs = new HashSet<string>();
		var expectedJobs = new HashSet<string>();

		// AnchorJob deliberately does not filter this loop. It selects which buff is allowed to
		// move the 2-minute anchor; narrowing the overlap counts to one job as well would mean
		// "sync to the Dancer" also stopped the rotation from noticing the other seven players.
		for (var index = 0; index < Buffs.Count; index++)
		{
			var buff = Buffs[index];
			var anchorActive = IsPartyBuffActive(buff);
			var isActive = buff.Type == StatusType.Buff ? anchorActive
				: SchedulerTarget?.HasStatus(false, buff.Ids) == true;
			if (InCombat && anchorActive) _ = _observedPartyBuffs.Add(buff);
			var shouldExpect = MCHSyncPlusPolicy.ShouldExpectPartyBuff(
				encounterTime, _observedPartyBuffs.Contains(buff));
			if (shouldExpect) names.Add($"{buff.JobAbbr} {buff.Name}");

			// Anchor significance counts distinct jobs, not statuses: BRD alone contributes two
			// tracked statuses (Battle Voice, Radiant Finale), which would otherwise let one job
			// satisfy IsSignificantPartyBurst by itself - exactly the single-job case it exists to
			// exclude. The overlap counters below stay status-based on purpose: full-overlap
			// detection needs to know whether both BRD buffs are up, not just that BRD is present.
			if (shouldExpect) expectedJobs.Add(buff.JobAbbr);
			if (anchorActive)
			{
				activeJobs.Add(buff.JobAbbr);
				anchorJobActive |= anchorJob is not null
					&& string.Equals(buff.JobAbbr, anchorJob, StringComparison.OrdinalIgnoreCase);
			}

			if (isActive)
			{
				shortestActiveRemaining = Math.Min(shortestActiveRemaining, PartyBuffRemaining(buff));
			}
			if (buff.JobAbbr == "NIN")
			{
				ninjaActive |= isActive;
				ninjaExpected |= shouldExpect;
				continue;
			}

			if (!shouldExpect) continue;
			expected++;
			if (isActive) active++;
		}
		if (ninjaExpected)
		{
			expected++;
			if (ninjaActive) active++;
		}
		_activePartyBuffs = active;
		_expectedPartyBuffsCount = expected;
		_anchorJobActive = anchorJobActive;
		_activeAnchorJobs = activeJobs.Count;
		_expectedAnchorJobs = expectedJobs.Count;
		_shortestActivePartyBuffRemaining = shortestActiveRemaining;

		// The scan-cache guard above only skips redundant work; whether to log is decided here,
		// by comparing the actual result, not a proxy for it.
		var expectedNames = string.Join(", ", names);
		if (expectedNames == _expectedPartyBuffs) return;

		_expectedPartyBuffs = expectedNames;
		Svc.Log.Info($"[MCH Sync] Watching party buffs: {(expectedNames.Length == 0 ? "none; personal burst" : expectedNames)}");
	}

	/// <summary>
	/// Observe raid cadence across bosses, even when the party debuffs a different enemy.
	/// Effective damage overlap and remaining duration deliberately use SchedulerTarget instead.
	/// </summary>
	private static IBattleChara? FindDebuffCarrier(StatusInfo buff)
	{
		if (CurrentTarget?.HasStatus(false, buff.Ids) == true) return CurrentTarget;
		if (HostileTarget?.HasStatus(false, buff.Ids) == true) return HostileTarget;

		var hostiles = DataCenter.AllHostileTargets;
		for (var index = 0; index < hostiles.Count; index++)
		{
			var hostile = hostiles[index];
			if (hostile.IsBossFromIcon() && hostile.HasStatus(false, buff.Ids)) return hostile;
		}
		return null;
	}

	private static bool IsPartyBuffActive(StatusInfo buff)
		=> buff.Type == StatusType.Buff
			? Player != null && StatusHelper.PlayerHasStatus(false, buff.Ids)
			: FindDebuffCarrier(buff) != null;

	private static float PartyBuffRemaining(StatusInfo buff)
	{
		if (buff.Type == StatusType.Buff)
		{
			return Player == null ? 0f : StatusHelper.PlayerStatusTime(false, buff.Ids);
		}

		return SchedulerTarget?.StatusTime(false, buff.Ids) ?? 0f;
	}

	/// <summary>
	/// True when the currently live buffs look like a genuine raid burst rather than one job
	/// using a cooldown off cycle. Only a significant window is allowed to move the anchor.
	///
	/// Both branches read the cached results of <see cref="UpdateExpectedPartyBuffs"/>, which every
	/// caller runs first. Rescanning here re-walked every hostile target for each tracked debuff on
	/// every frame, for an answer the cached pass had already computed.
	/// </summary>
	private bool IsAnchorWorthy()
		=> AnchorJob != MCHSyncPlusAnchorJob.Automatic
			? _anchorJobActive
			: MCHSyncPlusPolicy.IsSignificantPartyBurst(_activeAnchorJobs, _expectedAnchorJobs);

	private void UpdateDungeonTracker()
	{
		if (!AutomaticDungeon)
		{
			_dungeonTerritory = 0;
			_dungeonWasInCombat = InCombat;
			_dungeonBossEncounter = false;
			_dungeonBossCompleted = false;
			_bankDungeonBurst = false;
			_lastDungeonLogState = string.Empty;
			return;
		}

		if (_dungeonTerritory != DataCenter.TerritoryID)
		{
			_dungeonTerritory = DataCenter.TerritoryID;
			_dungeonWasInCombat = InCombat;
			_dungeonBossEncounter = IsCurrentTargetBoss;
			_dungeonBossCompleted = false;
			_bankDungeonBurst = false;
			LogDungeonState(_dungeonBossEncounter
				? "boss detected; full burst allowed"
				: "first trash burst allowed; Wildfire reserved for boss");
		}

		if (InCombat && IsCurrentTargetBoss)
		{
			_dungeonBossEncounter = true;
			LogDungeonState("boss detected; full burst allowed");
		}
		else if (InCombat && EncounterTime >= DungeonBossCombatFallback)
		{
			_dungeonBossEncounter = true;
			LogDungeonState("long combat; treating as a boss despite no boss icon");
		}
		else if (!InCombat && _dungeonWasInCombat)
		{
			_dungeonBossCompleted |= _dungeonBossEncounter;
			_bankDungeonBurst = !_dungeonBossEncounter;
			LogDungeonState(_bankDungeonBurst
				? "trash combat ended; banking until boss"
				: "boss combat ended; next trash burst allowed");
			_dungeonBossEncounter = false;
		}

		_dungeonWasInCombat = InCombat;
	}

	private void LogDungeonState(string state)
	{
		if (_lastDungeonLogState == state) return;

		_lastDungeonLogState = state;
		Svc.Log.Info($"[MCH Sync] Dungeon state: {state}");
	}

	/// <summary>
	/// UseQueen (and the EmergencyAbility overdrive check) can be evaluated more than once for
	/// the same real game frame - RotationSolver's next-action preview re-runs the same decision
	/// logic speculatively - so without this a single deployment logged five near-identical lines.
	/// Queen's own ~12s active duration makes a 1s debounce safe against ever collapsing two
	/// genuinely distinct events.
	/// </summary>
	private float _lastQueenLogAt = float.MinValue;

	private const int BatteryCap = 100;

	/// <summary>
	/// Last Battery value seen by the change log. -1 means "no baseline yet" so the first reading
	/// of a fight establishes one instead of reporting a spurious 0 -> N gain. Fight-scoped, and
	/// reset with the other per-fight trackers.
	/// </summary>
	private int _lastLoggedBattery = -1;
	private string _lastCappedBatterySource = string.Empty;
	private bool _queenDeployedThisFight;
	private bool _robotWasActive;

	/// <summary>
	/// The generator the last GCD implies, with the Battery it should have paid. Shared by the
	/// normal gain path and the at-cap loss path so both attribute a change the same way.
	/// </summary>
	private (string Source, int Expected) BatteryGainSource()
		=> IsLastGCD(false, ChainSawPvE) ? ("Chain Saw", 20)
			: IsLastGCD(false, ExcavatorPvE) ? ("Excavator", 20)
			: IsLastGCD(false, AirAnchorPvE) ? ("Air Anchor", 20)
			: IsLastGCD(false, CleanShotPvE, HeatedCleanShotPvE) ? ("Clean Shot", 10)
			: ("unknown", 0);

	/// <summary>
	/// Records every Battery gain with the generator that caused it and how much of it the 100 cap
	/// swallowed, which is the one thing the xivanalysis overcap number cannot tell us: whether the
	/// loss happened because Queen was still out (IsRobotActive blocks UseQueen outright, so no
	/// deploy is possible no matter what the gauge does) or because a deployable Queen was held.
	/// Generic on purpose - it reports on any encounter, so Vamp Fatale, Red Hot and Deep Blue,
	/// The Tyrant and Lindwurm are all covered without per-fight gating.
	/// </summary>
	private void LogBatteryChange()
	{
		if (!LogBurstReport) return;

		var battery = Battery;
		if (_lastLoggedBattery < 0)
		{
			_lastLoggedBattery = battery;
			return;
		}
		if (battery == _lastLoggedBattery)
		{
			// At a full gauge a generator applies zero, so the value never moves and this method
			// used to return here - blind to precisely the overcap it exists to record. One pull on
			// Lindwurm lost 40 Battery this way: the gauge sat at 100 for 10.4s while the TTK guard
			// rejected Queen, and our log showed nothing while xivanalysis flagged it Major.
			// Attributed once per generator GCD so a stall does not spam a line per frame.
			if (battery < BatteryCap) return;

			var (cappedSource, cappedExpected) = BatteryGainSource();
			if (cappedExpected == 0)
			{
				_lastCappedBatterySource = string.Empty;
				return;
			}
			if (cappedSource == _lastCappedBatterySource) return;

			_lastCappedBatterySource = cappedSource;
			Svc.Log.Info(
				$"[MCH Sync] {EncounterTime:F1}s Battery {battery} at cap, LOST {cappedExpected}"
				+ $" to cap from {cappedSource}, Queen active: {IsRobotActive}"
				+ $", plan: {_decision.Resources}");
			return;
		}

		var previous = _lastLoggedBattery;
		_lastLoggedBattery = battery;

		// A decrease is the Queen that actually landed consuming the gauge. OnQueenDeployed logs
		// only the selection that preceded it, which may not have been cast at all, so this drop
		// is the real event - it needs no second line here.
		if (battery < previous)
		{
			_lastCappedBatterySource = string.Empty;
			return;
		}

		var (source, expected) = BatteryGainSource();

		// Claim this GCD before the at-cap branch can see it. A gain that lands exactly on 100
		// leaves the gauge full with this same generator still the last GCD, so the next frame
		// would re-report a paid-in-full gain as a loss - which is what every LOST line in the
		// Lindwurm log actually was.
		_lastCappedBatterySource = source;

		var gained = battery - previous;
		var lost = expected > 0 ? Math.Max(0, expected - gained) : 0;
		Svc.Log.Info(
			$"[MCH Sync] {EncounterTime:F1}s Battery {previous} -> {battery} (+{gained}"
			+ (expected > 0 ? $" of {expected}" : string.Empty)
			+ (lost > 0 ? $", LOST {lost} to cap" : string.Empty)
			+ $") from {source}, Queen active: {IsRobotActive}, plan: {_decision.Resources}");
	}

	private string _lastRotationState = string.Empty;

	/// <summary>
	/// The whole rotation's gate state in one line, covering every resource and every hook - not
	/// just Queen. ActionTracer records what happened once an action reached CanUse; these gates
	/// run *before* that, so an action this rotation suppresses never reaches the tracer at all and
	/// leaves no evidence anywhere. Emitted only when the composed state actually changes, which
	/// keeps a full pull to a few hundred lines rather than one per frame.
	/// </summary>
	private void LogRotationState()
	{
		if (!LogBurstReport) return;

		// Decision is frame-cached and the two trackers are idempotent per frame, so every value
		// below is a read of work this frame already did.
		var state =
			$"gcd[wf:{(ShouldUseWildfire ? 1 : 0)} hc:{(ShouldUseHypercharge ? 1 : 0)} "
			+ $"fmf:{(ShouldUseFullMetalField ? 1 : 0)} burst:{(ShouldCommitPersonalBurst ? 1 : 0)}] "
			+ $"res[bank:{(ShouldHoldFlexibleResources ? 1 : 0)} rea:{(ShouldHoldReassembleForBuffs ? 1 : 0)} "
			+ $"spendrea:{(ShouldSpendRemainingReassemble ? 1 : 0)} qhold:{(ShouldHoldQueen ? 1 : 0)} "
			+ $"qtarget:{QueenBatteryTarget}] "
			+ $"gauge[heat:{Heat} batt:{Battery} oh:{(IsOverheated ? 1 : 0)} fmm:{(HasFullMetalMachinist ? 1 : 0)} "
			+ $"robot:{(IsRobotActive ? 1 : 0)} wfup:{(HasWildfire ? 1 : 0)}] "
			+ $"plan[{_decision.Wildfire}/{_decision.Resources}: {MCHSyncPlusPolicy.StableDecisionReason(_decision.Reason)}] "
			+ $"buffs[{_activePartyBuffs}/{_expectedPartyBuffsCount}]";

		if (state == _lastRotationState) return;

		_lastRotationState = state;
		Svc.Log.Info($"[MCH Sync] {EncounterTime:F1}s {state}");
	}

	private void LogQueenTimelineState(string trigger)
	{
		if (!LogBurstReport) return;

		var now = EncounterTime;
		if (now - _lastQueenLogAt < 1f) return;
		_lastQueenLogAt = now;

		var moduleName = TimelineAvailable ? DataCenter.BMRActiveModuleName ?? "active, unnamed" : "none";
		// "bound" only reports whether BossModReborn registers the IPC method at all. A bound
		// endpoint reading unknown means this encounter genuinely has no such window right now;
		// an unbound one means the installed BossModReborn build doesn't implement it, which reads
		// identically as "unknown" everywhere else in this rotation with no way to tell them apart.
		Svc.Log.Info(
			$"[MCH Sync] {now:F1}s Queen {trigger} - timeline module: {moduleName}, "
			+ $"downtime in: {Describe(DowntimeIn)} (bound: {DataCenter.BMRDebugTimelineDowntimeFunc}), "
			+ $"vulnerable in: {Describe(BMRVulnerableIn)} (bound: {DataCenter.BMRDebugTimelineVulnFunc}), "
			+ $"vulnerable end in: {Describe(BMRVulnerableEndIn)} (bound: {DataCenter.BMRDebugTimelineVulnEndFunc})");
	}
	#endregion

	public override void DisplayRotationStatus()
	{
		base.DisplayRotationStatus();
		var decision = Decision;

		ImGui.Text($"MCH Sync: {decision.Wildfire} - {decision.Reason}");
		ImGui.Text($"Resources: {(decision.Resources == MCHSyncPlusResourcePlan.Bank ? "banking" : "spending")}");

		var plan = SyncPlusBurstPlan switch
		{
			MCHSyncPlusPlan.Automatic => "Automatic (timeline + dungeons)",
			MCHSyncPlusPlan.TrustParty => "Trust party buffs",
			_ => "Solo / ignore party buffs",
		};
		ImGui.Text($"Plan: {plan}");
		ImGui.Text($"Scheduler: {(TimelineAvailable ? $"BossMod timeline ({DataCenter.BMRActiveModuleName ?? "active"})" : "party-buff prediction (no timeline)")}");
		ImGui.Text($"Watching: {(_expectedPartyBuffs?.Length > 0 ? _expectedPartyBuffs : "no party buffs")} ({_activePartyBuffs}/{_expectedPartyBuffsCount} live)");

		if (TimelineAvailable)
		{
			ImGui.Text($"Next downtime: {Describe(DowntimeIn)}   Next vulnerability: {Describe(BMRVulnerableIn)}");
		}
		else
		{
			var expected = SecondsUntilExpectedPartyBurst;
			ImGui.Text(expected.HasValue
				? $"Predicted party burst in: {expected.Value:F1}s"
				: "Predicted party burst in: learning first window");
		}

		if (WildfirePvE.Cooldown.HasOneCharge && WildfireHeldFor > 0f)
		{
			ImGui.Text($"Wildfire held for: {WildfireHeldFor:F1}s of {MaximumWildfireHoldSeconds:F0}s");
		}
		if (_wildfireUses.Count > 0)
		{
			ImGui.Text($"Wildfire uses this fight: {_wildfireUses.Count}");
		}
	}

	private static string Describe(float seconds)
		=> seconds is <= 0f or float.MaxValue ? "unknown" : $"{seconds:F1}s";
}

#region Types
internal enum MCHSyncPlusPlan
{
	// "Normal / safe sync" was removed: it differed from Automatic in exactly one place
	// (AutomaticDungeon, which only triggers with ContentType == Dungeons), so outside a dungeon the
	// two were the same mode wearing two names. The retired classic rotation's saved string for it
	// now migrates onto Automatic instead - see NormalizeRetiredMchSyncBurstPlan in RotationUpdater.
	[Description("Automatic (timeline + dungeons)")]
	Automatic,

	[Description("Trust party buffs")]
	TrustParty,

	[Description("Solo / ignore party buffs")]
	Personal,
}

/// <summary>Optional single-job source allowed to move the learned raid-buff anchor.</summary>
internal enum MCHSyncPlusAnchorJob
{
	[Description("Automatic (significant overlap)")]
	Automatic,

	AST,
	BRD,
	DNC,
	DRG,
	MNK,
	NIN,
	PCT,
	RDM,
	RPR,
	SCH,
	SMN,
}

/// <summary>Where Wildfire is weaved relative to the Full Metal Field GCD.</summary>
internal enum MCHSyncPlusWildfireWeave
{
	[Description("Before Full Metal Field, every burst")]
	EveryBurst,

	[Description("After Full Metal Field (Reborn default)")]
	AfterFullMetal,
}

/// <summary>Whether Wildfire may be released this frame.</summary>
internal enum MCHSyncPlusBurstGate
{
	/// <summary>Burst is off; the base rotation's own priorities apply.</summary>
	Free,

	/// <summary>Withhold Wildfire.</summary>
	Hold,

	/// <summary>Release Wildfire.</summary>
	Commit,
}

/// <summary>
/// Whether flexible resources - Heat, Battery, the banked Drill charge, Reassemble - should be
/// saved for an upcoming window or spent now. Deliberately independent of the Wildfire gate:
/// ahead of downtime the correct answer is hold Wildfire but spend everything else.
/// </summary>
internal enum MCHSyncPlusResourcePlan
{
	Spend,
	Bank,
}

/// <summary>
/// The downtime branch a decision came from, for the callers that must act on it. Kept separate
/// from Reason so the human-readable message stays free to change without moving rotation
/// behaviour: three action gates used to compare against that display string directly.
/// </summary>
internal enum MCHSyncPlusDowntimeAction
{
	None,

	/// <summary>Downtime is too close for a payload; spend flexible resources now.</summary>
	Dump,

	/// <summary>The hold ceiling forced a release the downtime signal still disagrees with.</summary>
	HoldCeiling,
}

internal readonly record struct MCHSyncPlusDecision(
	MCHSyncPlusBurstGate Wildfire,
	MCHSyncPlusResourcePlan Resources,
	string Reason,
	MCHSyncPlusDowntimeAction Downtime = MCHSyncPlusDowntimeAction.None)
{
	internal static MCHSyncPlusDecision Default
		=> new(MCHSyncPlusBurstGate.Free, MCHSyncPlusResourcePlan.Spend, "starting up");
}

internal readonly record struct MCHSyncPlusWildfireUse(
	float At,
	int ActiveBuffs,
	int ExpectedBuffs,
	string Reason,
	bool InVulnerabilityWindow,
	float DowntimeIn);

internal readonly record struct MCHSyncPlusSnapshot(
	bool AutoBurst,
	bool Reserved,
	bool SynchronizeParty,
	bool RequireLiveParty,
	bool WildfireReady,
	bool WildfireReadySoon,
	float WildfireHeldFor,
	bool TimelineActive,
	float DowntimeIn,
	float VulnerableIn,
	float VulnerableEndIn,
	bool PartyBurstActive,
	bool FullPartyBurstActive,
	float SecondsInPartyBurst,
	float? PredictedPartyBurstIn,
	float PayloadWindow,
	float MaximumHold,
	float OverlapWait,
	float LookAhead,
	bool ObjectiveBanked = false);
#endregion

/// <summary>
/// The whole scheduling decision, as one pure function over a snapshot. Every branch names its
/// reason, which drives both the status panel and the post-fight report.
/// </summary>
internal static class MCHSyncPlusScheduler
{
	internal const string DowntimeDumpReason = "downtime too close for a full window; spending resources";
	internal const string DowntimeHoldCeilingReason = "downtime hold ceiling reached; releasing";
	internal const string NoBurstReason = "no burst confirmed; Wildfire and Barrel wait for raid buffs";
	internal const string PhaseObjectiveReason = "phase objective secured; banking for the next phase";

	private static bool Known(float seconds) => seconds is > 0f and < float.MaxValue;

	internal static MCHSyncPlusDecision Evaluate(MCHSyncPlusSnapshot s)
	{
		if (!s.AutoBurst)
		{
			return new(MCHSyncPlusBurstGate.Free, MCHSyncPlusResourcePlan.Spend, "burst is off");
		}
		if (s.Reserved)
		{
			return new(MCHSyncPlusBurstGate.Hold, MCHSyncPlusResourcePlan.Bank, "reserved for the next boss");
		}
		// Ahead of the downtime dump and outside the hold ceiling: damage past the phase objective is
		// worthless, and everything not spent here is still there when the next phase starts.
		if (s.ObjectiveBanked)
		{
			return new(MCHSyncPlusBurstGate.Hold, MCHSyncPlusResourcePlan.Bank, PhaseObjectiveReason);
		}

		// Wildfire and Barrel Stabilizer are burst-only: with party sync on they commit only into a
		// confirmed burst (live raid buffs or an open vulnerability window). No hold ceiling, no
		// pre-downtime release, no "nothing to wait for" default: each of those fired them with no
		// buffs (logs: the Ice Veil ghost Wildfire, and the P4-end Wildfire that left P5 without one).
		static MCHSyncPlusDecision Hold(string reason, MCHSyncPlusResourcePlan resources)
			=> new(MCHSyncPlusBurstGate.Hold, resources, reason);

		// 1. Downtime: resources banked into a phase change are wasted, so dump them; Wildfire waits.
		if (s.TimelineActive && s.DowntimeIn is >= 0f and < float.MaxValue && s.DowntimeIn < s.PayloadWindow)
		{
			return new(MCHSyncPlusBurstGate.Hold, MCHSyncPlusResourcePlan.Spend, DowntimeDumpReason,
				MCHSyncPlusDowntimeAction.Dump);
		}

		// 2. Vulnerability windows outrank raid buffs when the timeline knows about one.
		//    "Currently inside" is not VulnerableIn near zero - BossMod's NextVulnerableIn always
		//    searches for the next window's START, so once truly inside one it skips past to the
		//    next occurrence (or infinity) instead of reporting anything close to zero. What does
		//    reflect the active window is NextVulnerableEndIn, which matches the current state's
		//    own end transition: inside a window, its end is always sooner than any future start.
		if (s.TimelineActive)
		{
			var insideWindow = Known(s.VulnerableEndIn)
				&& (!Known(s.VulnerableIn) || s.VulnerableEndIn <= s.VulnerableIn);

			// Being inside a window does not mean it is worth committing into - VulnerableEndIn is
			// exactly the remaining duration while inside one, so a window closing in a fraction of
			// a second gets less than a fraction of Wildfire's payload out of it. Same usefulness
			// bar as an approaching window, so a window is never "worth it" on the way in but "not
			// worth it" once already inside.
			if (insideWindow && s.WildfireReady && s.VulnerableEndIn >= s.PayloadWindow * 0.5f)
			{
				return new(MCHSyncPlusBurstGate.Commit, MCHSyncPlusResourcePlan.Spend,
					"vulnerability window open");
			}

			if (!insideWindow && Known(s.VulnerableIn))
			{
				var windowLength = Known(s.VulnerableEndIn)
					? s.VulnerableEndIn - s.VulnerableIn
					: float.MaxValue;
				var windowIsUseful = windowLength >= s.PayloadWindow * 0.5f;
				if (windowIsUseful && s.VulnerableIn <= s.LookAhead)
				{
					// Only wait for a future window when nothing better is already on offer. The
					// timeline says when a window opens, not how much it is worth, so it cannot
					// justify giving up raid buffs live right now - those fall through to step 3.
					if (!s.PartyBurstActive)
					{
						return Hold($"vulnerability window in {s.VulnerableIn:F1}s", MCHSyncPlusResourcePlan.Bank);
					}
				}
			}
		}

		// 3. Raid buff overlap. Gated on SynchronizeParty so Personal mode and an Automatic
		//    dungeon boss - both of which intentionally ignore party sync - don't end up waiting
		//    on a buff that happened to be live from an unrelated source.
		if (s.SynchronizeParty && s.PartyBurstActive)
		{
			var noTimeToWait = s.TimelineActive && s.DowntimeIn is >= 0f and < float.MaxValue
				&& Math.Max(0f, s.OverlapWait - s.SecondsInPartyBurst) + s.PayloadWindow > s.DowntimeIn;
			if (s.FullPartyBurstActive || s.OverlapWait <= 0f || s.SecondsInPartyBurst >= s.OverlapWait || noTimeToWait)
			{
				return new(MCHSyncPlusBurstGate.Commit, MCHSyncPlusResourcePlan.Spend, "raid buffs are up");
			}
			return Hold("waiting for the rest of the raid buffs", MCHSyncPlusResourcePlan.Bank);
		}

		if (!s.SynchronizeParty)
		{
			return new(MCHSyncPlusBurstGate.Commit, MCHSyncPlusResourcePlan.Spend, "no party sync; personal burst");
		}

		// 4. Fallback prediction, used when the timeline cannot help.
		if (s.RequireLiveParty && s.WildfireReadySoon)
		{
			return Hold("waiting for a live raid buff", MCHSyncPlusResourcePlan.Bank);
		}
		if (s.WildfireReadySoon && s.PredictedPartyBurstIn.HasValue)
		{
			var predicted = s.PredictedPartyBurstIn.Value;
			if (predicted <= s.LookAhead && predicted >= -s.MaximumHold)
			{
				return Hold($"raid buffs predicted in {predicted:F1}s", MCHSyncPlusResourcePlan.Bank);
			}
		}

		return Hold(NoBurstReason, MCHSyncPlusResourcePlan.Spend);
	}
}

internal static class MCHSyncPlusPolicy
{
	/// <summary>
	/// HP ratio at which damage on an FRU target stops being progress, or null for targets without a
	/// next phase (P5) or without a requirement (Ice Veil: the intermission ends on its own timer).
	/// Measured over 127 logged pulls: P2 and P3 end on a timer and only check HP - passes ended at
	/// up to 19.9% (P2) and 18.9% (P3), the wipes at 20.0% and 19.3%+ - so both keep a margin under
	/// the last observed pass. P1 and the P4 pair must die. The crystals stay a normal burst target:
	/// all four top-ranked MCH logs put pot + Wildfire on a Crystal of Light.
	/// </summary>
	internal static float? PhaseObjective(uint baseId) => baseId switch
	{
		0x459F => 0.18f, // P2 Usurper of Frost
		0x45A7 => 0.17f, // P3 Oracle of Darkness
		0x459B or 0x45A9 or 0x45AB => 0f, // P1 Fatebreaker, P4 Usurper/Oracle
		_ => null,
	};

	/// <summary>GetTTK is a linear HP extrapolation, so the time to a goal is the same line cut short.</summary>
	internal static float ObjectiveIn(float hpRatio, float ttk, float? goal)
	{
		if (goal is not float g) return ttk;
		if (hpRatio <= g) return 0f;
		return float.IsNaN(ttk) || hpRatio <= 0f ? float.NaN : ttk * (hpRatio - g) / hpRatio;
	}

	internal const float RecentTTKSpan = 4f;
	// Burst openers make the recent rate read fast: at <10s it held 6 good crystal Wildfires for 6 saved
	// (crystal logs; one pull lost one outright). At <8s: 4 saved, 0 wrongly held.
	internal const float RecentTTKHoldBelow = 8f;

	/// <summary>Kill time from the HP lost over the last few seconds; NaN when too short or not dropping.</summary>
	internal static float RecentTTK(float thenRatio, float nowRatio, float seconds)
		=> seconds >= 2f && thenRatio > nowRatio ? nowRatio * seconds / (thenRatio - nowRatio) : float.NaN;

	/// <summary>Barrel 15s before uptime only if its 120s recast is back for the next burst (10s slack: top logs 8s).</summary>
	internal static bool ShouldUseDowntimeBarrel(float uptimeIn, float? nextBurstIn)
		=> uptimeIn is > 0f and <= 15f && nextBurstIn is float next && next + 10f >= 120f;

	internal static bool ShouldPreSummonQueen(float uptimeIn, int battery, bool robotActive)
		=> !robotActive && battery >= 50 && uptimeIn is > 0f and <= 4.5f;

	/// <summary>FRU P3 Oracle and the P4 pair: the phases whose Barrel has to run ahead for the extra CT Barrel.</summary>
	internal static bool BarrelOnCooldownPhase(uint baseId) => baseId is 0x45A7 or 0x45A9 or 0x45AB;

	internal static bool BarrelBuffsUnclaimed(float buffsLeft, float? nextBurstIn)
		=> buffsLeft > 0f && (nextBurstIn is not float next || next > buffsLeft);

	/// <summary>How long until a cooldown is back where it is now, if a charge is spent now.</summary>
	internal static float ToolRefillAfterUse(float recastRemainToFull, float recastOneCharge)
		=> recastRemainToFull + recastOneCharge;

	private const float QueenScheduleCycle = 120f;
	private const int ExcavatorBatteryGain = 20;

	/// <summary>
	/// Normal raid-buff drift accepted around each learned 120-second recurrence. This is a
	/// cadence property, not permission to hold Wildfire, so it must not inherit the configurable
	/// Wildfire hold ceiling.
	/// </summary>
	internal const float PartyBurstRecurrenceTolerance = 30f;

	internal static bool ShouldHoldFullMetalForWildfire(
		bool autoBurst,
		bool wildfireReadySoon,
		bool wildfireReady,
		bool commitWildfire,
		bool downtimeBlocksWildfire)
		=> autoBurst && wildfireReadySoon
			&& (!wildfireReady || !commitWildfire || downtimeBlocksWildfire);

	/// <summary>
	/// Keep Full Metal Field from consuming its buff during an accepted burst frame in which
	/// Wildfire must lead. This covers the Barrel Stabilizer seam: Barrel creates Full Metal
	/// Machinist in the weave slot, then the base rotation would otherwise select Full Metal Field
	/// immediately and leave no legal pre-GCD Wildfire weave.
	/// </summary>
	internal static bool ShouldDelayFullMetalForLeadingWildfire(
		bool shouldUseWildfire,
		bool wildfireReady,
		bool hasFullMetalMachinist,
		bool payloadReady,
		bool downtimeSoon)
		=> shouldUseWildfire && wildfireReady
			&& hasFullMetalMachinist && payloadReady && !downtimeSoon;

	internal static bool ShouldHoldHyperchargeForFullMetalWildfire(
		bool fullMetalRelevant,
		bool shouldUseWildfire,
		bool wildfireReady,
		bool payloadReady)
		// EmergencyAbility is evaluated before AttackAbility, so releasing this hold when the
		// weave opens lets capped Hypercharge steal Wildfire's slot. The cooldown changing is the
		// smallest reliable proof that Wildfire was actually committed.
		=> fullMetalRelevant && shouldUseWildfire && wildfireReady && payloadReady;

	internal static string StableDecisionReason(string reason)
		=> reason.StartsWith("raid buffs predicted in ", StringComparison.Ordinal)
			? "raid buffs predicted"
			: reason.StartsWith("vulnerability window in ", StringComparison.Ordinal)
				? "vulnerability window"
				: reason;

	internal static string DescribeWildfireCastState(int activeBuffs, int expectedBuffs)
		=> expectedBuffs > 0 && activeBuffs >= expectedBuffs
			? "full tracked overlap at cast"
			: activeBuffs > 0
				? "partial tracked overlap at cast"
				: "before tracked buffs at cast";

	internal static bool ShouldExpectPartyBuff(float encounterTime, bool observedThisFight)
		=> encounterTime <= 30f || observedThisFight;

	/// <summary>
	/// The first worthy window anchors immediately. Later windows must recur near an integer
	/// multiple of the learned cycle, within the fixed cadence tolerance.
	/// </summary>
	internal static bool ShouldObservePartyBurst(
		float lastObservedAt, float observedAt, float cycle, bool fullBurstAfterInterruption = false)
	{
		if (lastObservedAt < 0f) return true;

		var elapsed = observedAt - lastObservedAt;
		if (fullBurstAfterInterruption && elapsed > PartyBurstRecurrenceTolerance) return true;
		var cycles = MathF.Round(elapsed / cycle);
		return cycles >= 1f
			&& MathF.Abs(elapsed - cycles * cycle) <= PartyBurstRecurrenceTolerance;
	}

	internal static bool ShouldHardHoldQueen(
		bool timelineAvailable,
		bool downtimeWithinExecutionWindow)
		=> timelineAvailable && downtimeWithinExecutionWindow;

	/// <summary>
	/// Reassemble has no payload window to protect the way Wildfire does - a held charge is either
	/// spent this frame or it is not, so there is nothing here but the three conditions that make
	/// holding free: buffs are not already up (spend immediately once they are - waiting further
	/// gains nothing), a charge would not overcap while held, and the predicted window remains
	/// inside the scheduler's hard hold ceiling.
	/// </summary>
	internal static bool ShouldHoldReassembleForBuffs(
		bool synchronizeParty,
		bool partyBurstActive,
		bool atMaxCharges,
		float? predictedPartyBurstIn,
		float maximumHold)
		=> synchronizeParty
			&& !partyBurstActive
			&& !atMaxCharges
			&& predictedPartyBurstIn is > 0f and float predicted
			&& predicted <= maximumHold;

	internal static bool ShouldSpendReassembleInPartyBurst(bool partyBurstActive, bool resourcesSpend)
		=> partyBurstActive && resourcesSpend;

	/// <summary>
	/// Full-uptime Queen targets expressed relative to the learned party-burst anchor. The opener
	/// keeps its established 60-Battery release. The first odd minute consolidates at 90; later
	/// cycles spend a genuine 50-Battery surplus followed by the odd-minute release, then reserve
	/// 100 for the next two-minute window.
	///
	/// The odd-minute release cycles 60 -> 70 -> 80 and then returns to 60, per The Balance's
	/// current MCH guide: the 3rd minute is 60, the 5th 70, the 7th 80, then it loops back to 60
	/// at 9 minutes. It does NOT climb to 90 and stay there - a saturating target delays or loses
	/// the nine-minute Queen in any fight that runs long.
	/// </summary>
	internal static int QueenBatteryTarget(
		bool synchronizePartyBurst,
		bool partyBurstActive,
		bool openerRelease,
		float? secondsSincePartyBurst,
		int partyBurstCycle)
	{
		if (openerRelease) return 60;
		// Personal mode has no buff window to align to, so there is nothing to spend a partial
		// Queen for - the only thing that costs damage is overcapping the gauge, and the hard hold
		// below stands down as soon as the next generator would do that. Queen potency is linear
		// at 266 per 10 Battery (The Balance), so a full gauge is simply the most that can be
		// banked before the cap forces a deploy.
		if (!synchronizePartyBurst) return 100;
		if (partyBurstActive) return 100;
		if (secondsSincePartyBurst is null || partyBurstCycle <= 0)
		{
			return 90;
		}

		var phase = Math.Clamp(secondsSincePartyBurst.Value, 0f, QueenScheduleCycle - 0.001f);
		if (partyBurstCycle == 1)
		{
			return phase < 90f ? 90 : 100;
		}

		if (phase < 30f) return 100;
		if (phase < 60f) return 50;
		// partyBurstCycle 2 is the 3-minute release, so the ramp is indexed from there and wraps
		// every three cycles: 3min 60, 5min 70, 7min 80, 9min 60 again.
		if (phase < 90f) return 60 + ((partyBurstCycle - 2) % 3) * 10;
		return 100;
	}

	/// <summary>
	/// Holds Battery for a higher scheduled target - unless the generators already queued would
	/// carry the gauge past 100 first. Holding through an overcap reserves nothing: the surplus is
	/// lost either way, and deploying now at least spends it.
	/// </summary>
	internal static bool ShouldHoldQueenForBatteryTarget(
		int battery, int batteryTarget, int projectedBatteryGain)
		=> battery < batteryTarget && battery + projectedBatteryGain <= 100;

	internal static bool ShouldReleaseFinalQueen(int battery, bool robotActive,
		float targetRemaining, float requiredLifetime, float margin, float downtimeIn)
		=> battery >= 50 && !robotActive && targetRemaining >= requiredLifetime
			&& targetRemaining <= requiredLifetime + margin && downtimeIn >= requiredLifetime;

	internal static bool ShouldHoldQueenForLifetime(float targetRemaining, float requiredLifetime)
		=> !float.IsNaN(targetRemaining) && targetRemaining < requiredLifetime;

	internal static bool ShouldReleaseQueenAtBatteryTarget(int battery, int batteryTarget)
		=> batteryTarget is >= 50 and < 90 && battery >= batteryTarget;

	internal static int AdvancePartyBurstCycle(
		int currentCycle,
		float lastObservedAt,
		float observedAt,
		float cycle)
	{
		if (lastObservedAt < 0f) return 1;

		var elapsedCycles = Math.Max(1, (int)MathF.Round((observedAt - lastObservedAt) / cycle));
		return currentCycle + elapsedCycles;
	}

	/// <summary>
	/// Excavator pays a fixed 20 Battery. When the scheduled Queen wants a target that 20 would
	/// overshoot, holding it lets a 10-Battery Clean Shot land the gauge exactly - the delay The
	/// Balance prescribes for the odd-minute Queens ("instead of following up with Excavator and
	/// getting 70, we can delay it for a full main combo"). Excavator holds for 30s, so this costs
	/// nothing; the ExcavatorReady expiry check in the base rotation is the backstop.
	///
	/// Never holds inside a party burst: there the 660 potency landing under buffs outweighs any
	/// gauge tidiness, which is the guide's other rule for the even minutes.
	/// </summary>
	internal static bool ShouldHoldExcavatorForBattery(
		bool partyBurstActive, bool hasFullMetalMachinist, int battery, int batteryTarget)
		=> !partyBurstActive
			// Never while Full Metal Machinist is banked. The base rotation gates Full Metal Field
			// on !HasExcavatorReady, an assumption that only held while Excavator was spent
			// unconditionally on sight - so holding Excavator silently blocks a 945-effective-potency
			// Full Metal Field for the whole hold. Gauge precision is worth ~10 Battery; this is not
			// a trade, it is a loss.
			&& !hasFullMetalMachinist
			&& battery < batteryTarget
			&& battery + ExcavatorBatteryGain > batteryTarget;

	internal static bool ShouldOverdriveQueen(
		bool robotActive,
		bool targetEndsBeforeQueen,
		bool uptimeEndsBeforeQueen)
		=> robotActive && (targetEndsBeforeQueen || uptimeEndsBeforeQueen);

	/// <summary>
	/// Whether a live buff window is substantial enough to move the 2-minute anchor. A lone
	/// cooldown used off cycle - one Dokumori on a trash pull, a Ninja bursting solo - should not
	/// redefine when the whole party is expected to burst next.
	///
	/// Takes distinct-job counts, not status counts. BRD alone contributes two tracked statuses
	/// (Battle Voice, Radiant Finale); counting statuses would let a single BRD satisfy the "half
	/// the party" test by itself, which is exactly the single-job case this exists to reject. The
	/// caller counts every other overlap decision by status, since full-overlap detection needs to
	/// know whether both BRD buffs are up - only this call needs the job-scoped counts.
	/// </summary>
	/// The "expectedJobs &lt;= 1" special case that used to sit here was dead: activeJobs is already
	/// at least 1, so activeJobs * 2 is at least 2, which clears any expectedJobs of 0 or 1 on its
	/// own. Worth stating plainly rather than restoring - it means a one-buff-job party has NO
	/// single-job protection, because a majority of one job is one job. The guard only does real
	/// work from two expected jobs upward.
	internal static bool IsSignificantPartyBurst(int activeJobs, int expectedJobs)
		=> activeJobs > 0 && activeJobs * 2 >= expectedJobs;

	internal static bool ShouldReserve(
		bool autoBurst,
		bool bankDungeonBurst,
		bool protectNextBossBurst,
		bool bossEncounter)
		=> !autoBurst || ((bankDungeonBurst || protectNextBossBurst) && !bossEncounter);

	internal static bool ShouldUseWildfire(
		bool commitBurst,
		bool automaticDungeon,
		bool bossEncounter,
		bool targetEndsBeforePayload,
		bool targetAllowed)
		=> commitBurst && (!automaticDungeon || bossEncounter)
			&& !targetEndsBeforePayload && targetAllowed;

	internal const float DetonateWithin = 2f;
	internal const float DetonateMargin = 0.5f;
	internal const float QueenOverdriveLead = 3.5f; // Overdrive to Crowned Collider took 0.5-2.8s in logs

	/// <summary>The Wildfire target dies before (or right at) the natural detonation, and soon.</summary>
	internal static bool ShouldDetonateEarly(float recentTtk, float wildfireLeft)
		=> !float.IsNaN(recentTtk) && recentTtk <= DetonateWithin && recentTtk < wildfireLeft + DetonateMargin;

	/// <summary>The Queen's target dies soon enough that Pile Bunker and Crowned Collider need Overdrive now.</summary>
	internal static bool QueenTargetEndsSoon(float recentTtk, float summonRemaining)
		=> !float.IsNaN(recentTtk) && recentTtk <= QueenOverdriveLead && recentTtk < summonRemaining;

	internal static bool ShouldDetonateWildfire(
		bool hasWildfire,
		bool detonatorReady,
		bool targetDying,
		bool downtimeSoon)
		=> hasWildfire && detonatorReady && (targetDying || downtimeSoon);

	/// <summary>
	/// Heat 100 releases a fresh Hypercharge even while Full Metal Machinist is held - EXCEPT while
	/// Wildfire is live, where Full Metal Machinist still blocks it. That exception is deliberate,
	/// not an oversight: EveryBurst mode casts Wildfire while Full Metal Machinist is held on
	/// purpose, and Full Metal Field must win the GCD immediately after. Letting a capped Heat
	/// gauge steal that slot trades a 945-effective-potency Full Metal Field for the ~5 Heat that
	/// one GCD of overcap costs. The window lasts a single GCD, and the base rotation's
	/// ShouldForceCappedHypercharge encodes the same exception.
	///
	/// Dying-target and downtime guards block fresh windows until the scheduler deliberately
	/// releases them, but an active Wildfire keeps its payload until the Detonator fallback.
	///
	/// The Full Metal Machinist branch is bounded by payloadWindowClosing because it was written
	/// as a one-GCD protection and had no way to end after one GCD. Live evidence: the opener of
	/// A logged opener raised Full Metal Machinist off Barrel Stabilizer at t+2.5, cast Wildfire
	/// at t+5, and never resolved Full Metal Field inside the ten seconds that followed.
	/// Hypercharge logged ACCEPT on every frame from t+4 to t+33 and was never selected once, so
	/// the opener Wildfire ran its whole duration with no Blazing Shots in it: 4/6 GCDs and 92k
	/// against 142-152k for the three windows that paired correctly. Trading one GCD of Heat
	/// overcap for a 945-effective-potency Full Metal Field is the good deal this branch was
	/// written for; trading six GCDs of Wildfire for a Full Metal Field that never arrives is not.
	/// Once too little Wildfire remains to still land the payload, further waiting costs a Blazing
	/// Shot per GCD with nothing to show for it, so the hold ends and the dump goes out.
	/// </summary>
	/// A returning tool blocks a fresh window on the same terms as imminent downtime, and takes the
	/// same hold-ceiling escape. MCH_Reborn.ToolChargeSoon has always carried this eight-second
	/// test, but it guards one of the six Hypercharge call sites; the five inside
	/// MCH_Reborn.EmergencyAbility nest directly inside this property and had no tool test at all.
	/// Live evidence: the capped-Heat force path fired Hypercharge with
	/// Chain Saw three seconds out, and the five Blazing Shots pushed it 5.02s late. Heat overcap
	/// across a few GCDs is worth far less than a 20-Battery tool GCD delayed by a whole dump,
	/// which is the trade ToolChargeSoon already made for the one path it covered.
	///
	/// A live Wildfire is exempt, on the branch above: its payload is the entire reason the window
	/// exists, and delaying it costs Blazing Shots one for one against a ten-second buff.
	internal static bool ShouldUseHypercharge(
		bool targetDying, bool hasWildfire, bool downtimeSoon, bool releasingPastHoldCeiling,
		bool hasFullMetalMachinist, bool heatCapped, bool payloadWindowClosing,
		bool toolChargeSoon)
		=> hasWildfire
			? !hasFullMetalMachinist || payloadWindowClosing
			: !targetDying
				&& (!downtimeSoon || releasingPastHoldCeiling)
				&& (!toolChargeSoon || releasingPastHoldCeiling)
				&& (heatCapped || !hasFullMetalMachinist);

	/// <summary>
	/// Wildfire is weaved before Full Metal Field when that GCD is already selected or an accepted
	/// party-buff window is live. Full Metal Machinist proves the follow-up is available.
	/// </summary>
	internal static bool ShouldWeaveWildfireBeforeFullMetal(
		bool weaveEnabled,
		bool commitWildfire,
		bool wildfireReady,
		bool nextGcdIsFullMetalField,
		bool partyBurstActive,
		bool hasFullMetalMachinist,
		bool payloadReady,
		bool lateWeave,
		bool downtimeSoon)
		=> weaveEnabled && commitWildfire && wildfireReady
			&& (nextGcdIsFullMetalField || partyBurstActive) && hasFullMetalMachinist
			&& payloadReady && lateWeave && !downtimeSoon;

	/// <summary>Once Wildfire leads the window, Full Metal Field stays next until consumed.</summary>
	internal static bool ShouldPrioritizeFullMetalAfterWildfire(
		bool hasFullMetalMachinist,
		bool wildfireActive,
		bool wildfireWasLastAction)
		=> hasFullMetalMachinist && (wildfireActive || wildfireWasLastAction);

	internal static bool ShouldPrioritizeAirAnchor(
		bool wildfirePayloadPending,
		bool isOverheated,
		bool aoeTargeting)
		=> !wildfirePayloadPending && !isOverheated && !aoeTargeting;

	internal static bool ShouldWaitForWildfireHyperchargeGcd(
		bool wildfirePayloadPending,
		bool hyperchargeChargeReady,
		bool gcdQueueReady,
		float stallElapsed,
		float maximumStall)
		=> wildfirePayloadPending && hyperchargeChargeReady
			&& (!gcdQueueReady || stallElapsed < maximumStall);

	internal static bool ShouldUseOpenerMedicine(
		bool enabled,
		bool inCombat,
		float combatTime,
		bool wildfireReady,
		bool commitWildfire)
		=> enabled && inCombat && combatTime <= 15f && wildfireReady && commitWildfire;

	internal static bool ShouldUseSecondMedicine(
		bool enabled,
		bool inCombat,
		bool reserveBurst,
		float combatTime,
		float targetTime,
		float cycle,
		bool wildfireReady,
		bool commitBurst)
		=> enabled && inCombat && !reserveBurst
			&& combatTime >= targetTime - 15f
			&& combatTime <= targetTime + cycle
			&& wildfireReady && commitBurst;

	/// <summary>
	/// Zero means "do not wait": the earliest-expiring buff can no longer cover a full payload,
	/// so waiting for the rest of the window would cost more than the overlap gains.
	/// </summary>
	internal static float EffectiveOverlapWait(
		float shortestActiveBuffRemaining,
		float payloadWindow,
		float overlapWait)
		=> shortestActiveBuffRemaining <= payloadWindow + 1f ? 0f : overlapWait;
}
