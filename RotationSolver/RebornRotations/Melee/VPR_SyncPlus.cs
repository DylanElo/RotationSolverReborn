using System.ComponentModel;
using ECommons.DalamudServices;

namespace RotationSolver.RebornRotations.Melee;

/// <summary>
/// Canonical encounter-aware Viper rotation.
/// Burst scheduling (Reawaken, Serpent's Ire, and Uncoiled Fury) is isolated
/// behind a pure snapshot/decision boundary, matching the MCH Sync+ architecture.
/// </summary>
[Rotation("VPR Sync", CombatType.PvE, GameVersion = "7.55",
	Description = "Encounter-aware Viper burst using BossMod timelines when available, with safe raid-buff synchronization and Reawaken/Serpent Offering pooling.")]
public sealed class VPR_SyncPlus : VPR_Reborn
{
	private const float DungeonBossCombatFallback = 90f;
	private const float PartyBurstCycle = 120f;
	private const float ReawakenExecutionWindow = 9.5f;

	#region Config
	[RotationConfig(CombatType.PvE, Name = "Burst strategy",
		Section = "Burst", Order = 100,
		Tooltip = "Automatic uses encounter timelines, predicts future raid-buff windows, and reserves burst for the dungeon boss. Trust party buffs waits for a live raid buff instead of a prediction. Personal ignores party buffs entirely.")]
	private VPRSyncPlusPlan SyncPlusBurstPlan { get; set; } = VPRSyncPlusPlan.Automatic;

	[RotationConfig(CombatType.PvE, Name = "Use BossMod Reborn timeline",
		Section = "Burst", Order = 110,
		Tooltip = "Uses predicted downtime and vulnerability windows when the active BossMod module provides them. Falls back safely when timeline data is unavailable.")]
	private bool UseEncounterTimeline { get; set; } = true;

	[RotationConfig(CombatType.PvE, Name = "Use a second potion",
		Section = "Potions", Order = 210,
		Tooltip = "Allows a later potion when Serpent's Ire burst is ready inside the configured timing window.")]
	private bool UseSecondBurstMedicine { get; set; } = true;

	[Range(300f, 480f, ConfigUnitType.Seconds, 1f)]
	[RotationConfig(CombatType.PvE, Name = "Second potion time", Parent = nameof(UseSecondBurstMedicine),
		Section = "Potions", Order = 220,
		Tooltip = "Target encounter time for the later potion. The rotation still waits for a committed burst window.")]
	private float SecondMedicineTime { get; set; } = 360f;

	[RotationConfig(CombatType.PvE, Name = "Show advanced settings",
		Section = "Advanced", Order = 400,
		Tooltip = "Reveals optional synchronization, diagnostics, and timing controls. Defaults are recommended for normal use.")]
	private bool AdvancedTiming { get; set; } = false;

	[RotationConfig(CombatType.PvE, Name = "Raid-buff anchor", Parent = nameof(AdvancedTiming),
		Section = "Advanced", Order = 410,
		Tooltip = "Automatic accepts a significant overlap. Selecting a job lets only that job move the learned two-minute anchor; all party buffs are still counted for overlap.")]
	private VPRSyncPlusAnchorJob AnchorJob { get; set; } = VPRSyncPlusAnchorJob.Automatic;

	[RotationConfig(CombatType.PvE, Name = "Enable burst diagnostics", Parent = nameof(AdvancedTiming),
		Section = "Advanced", Order = 420,
		Tooltip = "Logs decision changes during combat and writes a burst summary after the fight. Records confirmed burst casts and decision changes for troubleshooting.")]
	private bool LogBurstReport { get; set; } = true;

	[Range(0f, 45f, ConfigUnitType.Seconds, 1f)]
	[RotationConfig(CombatType.PvE, Name = "Maximum burst hold", Parent = nameof(AdvancedTiming),
		Section = "Advanced", Order = 430,
		Tooltip = "Hard ceiling for delaying ready Reawaken while waiting for a better window.")]
	private float MaximumBurstHoldSeconds { get; set; } = 30f;

	[Range(0f, 10f, ConfigUnitType.Seconds, 0.5f)]
	[RotationConfig(CombatType.PvE, Name = "Partial-overlap wait", Parent = nameof(AdvancedTiming),
		Section = "Advanced", Order = 440,
		Tooltip = "Briefly waits for missing tracked raid buffs when the active buffs can still cover the burst payload.")]
	private float OverlapWaitSeconds { get; set; } = 6f;

	[Range(0f, 15f, ConfigUnitType.Seconds, 0.5f)]
	[RotationConfig(CombatType.PvE, Name = "Burst look-ahead", Parent = nameof(AdvancedTiming),
		Section = "Advanced", Order = 450,
		Tooltip = "How far ahead the scheduler considers predicted raid buffs or encounter opportunities.")]
	private float LookAheadSeconds { get; set; } = 8f;
	#endregion

	#region State
	private float _lastObservedPartyBurstAt = -1f;
	private int _observedPartyBurstCycle;
	private float _partyBurstStartedAt = -1f;
	private float _anchorWorthyStartedAt = -1f;
	private float _burstReadyAt = -1f;
	private float _lastEncounterTime = -1f;
	private bool _partyBurstWasActive;
	private bool _anchorWasWorthy;
	private bool _burstWasReady;
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
	private VPRSyncPlusDecision _decision = VPRSyncPlusDecision.Default;
	private float _decisionComputedAt = float.MinValue;
	private bool _decisionComputedInCombat;
	private readonly List<VPRSyncPlusBurstUse> _burstUses = [];
	private float _totalHeldSeconds;
	private string _lastReportedReasonKey = string.Empty;
	private string _pendingBurstReason = string.Empty;
	private int _pendingBurstActiveBuffs;
	private int _pendingBurstExpectedBuffs;
	#endregion

	private static float EncounterTime => DataCenter.CombatTimeRaw;
	private bool PartyBurstActive => _activePartyBuffs > 0;
	private bool SignificantPartyBurstActive => PartyBurstActive
		&& VPRSyncPlusPolicy.IsSignificantPartyBurst(_activeAnchorJobs, _expectedAnchorJobs);
	private bool FullPartyBurstActive => _expectedPartyBuffsCount > 0
		&& _activePartyBuffs >= _expectedPartyBuffsCount;
	private float SecondsInPartyBurst => _partyBurstStartedAt < 0f ? 0f : EncounterTime - _partyBurstStartedAt;
	private float BurstHeldFor => _burstReadyAt < 0f ? 0f : EncounterTime - _burstReadyAt;
	private static IBattleChara? SchedulerTarget => CurrentTarget ?? HostileTarget;
	private bool IsCurrentTargetBoss => SchedulerTarget?.IsBossFromIcon() == true;
	private bool TargetDying => SchedulerTarget?.IsDying() == true;

	private bool TargetEndsBeforeBurstPayload
	{
		get
		{
			var target = SchedulerTarget;
			var ttk = target?.GetTTK() ?? float.NaN;
			return !float.IsNaN(ttk) && ttk < ReawakenExecutionWindow;
		}
	}

	private bool HasPartyBurstSources
	{
		get
		{
			UpdateExpectedPartyBuffs();
			return _expectedPartyBuffsCount > 0;
		}
	}

	private bool AutomaticDungeon => SyncPlusBurstPlan == VPRSyncPlusPlan.Automatic
		&& DataCenter.Territory?.ContentType == RotationSolver.Basic.Data.TerritoryContentType.Dungeons;
	private bool SynchronizePartyBurst => SyncPlusBurstPlan != VPRSyncPlusPlan.Personal
		&& !AutomaticDungeon && HasPartyBurstSources;
	private bool TimelineEnabled => UseEncounterTimeline;
	private bool TimelineAvailable => TimelineEnabled && BMRActive;
	private float DowntimeIn => TimelineEnabled ? BMRDowntimeIn : float.MaxValue;
	private bool DowntimeWithin(float seconds) => TimelineEnabled && BMRDowntimeWithin(seconds);

	private bool ProtectNextDungeonBossBurst => AutomaticDungeon
		&& _dungeonBossCompleted
		&& !_dungeonBossEncounter
		&& !IsCurrentTargetBoss
		&& SerpentsIrePvE.EnoughLevel
		&& SerpentsIrePvE.Cooldown.WillHaveOneCharge(30f);

	private bool ReserveBurst
	{
		get
		{
			UpdateDungeonTracker();
			return VPRSyncPlusPolicy.ShouldReserve(
				IsBurst,
				AutomaticDungeon && _bankDungeonBurst,
				ProtectNextDungeonBossBurst,
				_dungeonBossEncounter || IsCurrentTargetBoss);
		}
	}

	private float? SecondsUntilExpectedPartyBurst
	{
		get
		{
			if (_lastObservedPartyBurstAt < 0f) return null;

			var hold = MaximumBurstHoldSeconds;
			var next = _lastObservedPartyBurstAt + PartyBurstCycle;
			while (next < EncounterTime - hold)
			{
				next += PartyBurstCycle;
			}
			return next - EncounterTime;
		}
	}

	private bool ReawakenReady => HasReadyToReawaken || (SerpentsLineageTrait.EnoughLevel && SerpentOffering >= 50);
	private float PlannedBurstWindow => VPRSyncPlusPolicy.PlannedBurstWindow(
		HasReadyToReawaken, SerpentOffering, ReawakenExecutionWindow, GCDTime(1));
	private bool FinishNormalCombo
	{
		get
		{
			var single = IsLastComboAction(ActionID.SteelFangsPvE, ActionID.ReavingFangsPvE,
				ActionID.HuntersStingPvE, ActionID.SwiftskinsStingPvE);
			var aoe = IsLastComboAction(ActionID.SteelMawPvE, ActionID.ReavingMawPvE,
				ActionID.HuntersBitePvE, ActionID.SwiftskinsBitePvE);
			var venom = single ? StatusHelper.PlayerStatusTime(true, StatusID.HindsbaneVenom,
				StatusID.HindstungVenom, StatusID.FlanksbaneVenom, StatusID.FlankstungVenom)
				: aoe ? StatusHelper.PlayerStatusTime(true, StatusID.GrimhuntersVenom, StatusID.GrimskinsVenom) : 0f;
			return VPRSyncPlusPolicy.ShouldFinishNormalCombo(single || aoe,
				VPRSyncPlusPolicy.ComboDeadline(LiveComboTime, venom), PlannedBurstWindow, GCDTime(1));
		}
	}

	private VPRSyncPlusDecision Decision
	{
		get
		{
			var now = EncounterTime;
			var inCombat = InCombat;
			if (now == _decisionComputedAt && inCombat == _decisionComputedInCombat) return _decision;

			_decisionComputedAt = now;
			_decisionComputedInCombat = inCombat;
			UpdateBurstTracker();
			UpdateDungeonTracker();

			_decision = VPRSyncPlusScheduler.Evaluate(new VPRSyncPlusSnapshot(
				IsBurst,
				ReserveBurst,
				SynchronizePartyBurst,
				SyncPlusBurstPlan == VPRSyncPlusPlan.TrustParty,
				ReawakenReady,
				ReawakenReady || (SerpentsIrePvE.Cooldown.WillHaveOneCharge(LookAheadSeconds)),
				BurstHeldFor,
				TimelineAvailable,
				DowntimeIn,
				TimelineEnabled ? BMRVulnerableIn : float.MaxValue,
				TimelineEnabled ? BMRVulnerableEndIn : float.MaxValue,
				PartyBurstActive,
				FullPartyBurstActive,
				SecondsInPartyBurst,
				SecondsUntilExpectedPartyBurst,
				ReawakenExecutionWindow,
				MaximumBurstHoldSeconds,
				VPRSyncPlusPolicy.EffectiveOverlapWait(
					_shortestActivePartyBuffRemaining,
					PlannedBurstWindow,
					OverlapWaitSeconds),
				LookAheadSeconds,
				HasReadyToReawaken));

			if (_decision.Burst == VPRSyncPlusBurstGate.Commit && ReawakenReady)
			{
				_pendingBurstReason = _decision.Reason;
				_pendingBurstActiveBuffs = _activePartyBuffs;
				_pendingBurstExpectedBuffs = _expectedPartyBuffsCount;
			}

			var reasonKey = VPRSyncPlusPolicy.StableDecisionReason(_decision.Reason);
			if (LogBurstReport && reasonKey != _lastReportedReasonKey && InCombat)
			{
				_lastReportedReasonKey = reasonKey;
				Svc.Log.Info($"[VPR Sync] {now:F1}s {_decision.Burst}/{_decision.Resources}: {_decision.Reason} (buffs {_activePartyBuffs}/{_expectedPartyBuffsCount}, Gauge {SerpentOffering}, Anguine {AnguineTributeStacks})");
			}

			RecordBurstCasts();
			LogRotationState();
			return _decision;
		}
	}

	#region Burst & Action Hooks
	private bool ShouldCommitPersonalBurst => Decision.Burst == VPRSyncPlusBurstGate.Commit;
	private bool ShouldHoldFlexibleResources => Decision.Resources == VPRSyncPlusResourcePlan.Bank;

	private bool ShouldUseReawaken
		=> VPRSyncPlusPolicy.ShouldUseReawaken(
			ShouldCommitPersonalBurst || (HasReadyToReawaken
				&& StatusHelper.PlayerWillStatusEnd(GCDTime(2), true, StatusID.ReadyToReawaken)),
			ReawakenReady,
			SwiftTime > SwiftTimer,
			HuntersTime > HuntersTimer,
			TargetEndsBeforeBurstPayload,
			!TargetDying,
			TimelineAvailable,
			DowntimeWithin(ReawakenExecutionWindow),
			SerpentOffering == 100,
			HasReadyToReawaken);

	private bool ShouldUseSerpentsIre
		=> VPRSyncPlusPolicy.ShouldUseSerpentsIre(
			IsBurst && !ReserveBurst,
			RattlingCoilStacks < MaxRattling,
			SerpentsIrePvE.Cooldown.HasOneCharge);

	protected override bool AllowSerpentsIre => ShouldUseSerpentsIre;
	protected override bool AllowReawaken => IsBurst && !FinishNormalCombo && ShouldUseReawaken
		&& !VPRSyncPlusPolicy.ShouldPoolGauge(SynchronizePartyBurst, PartyBurstActive,
			HasReadyToReawaken, SerpentOffering,
			SerpentsIrePvE.EnoughLevel && SerpentsIrePvE.IsEnabled && SerpentsIrePvE.Cooldown.ElapsedAfter(ReawakenDelayTimer), SecondsUntilExpectedPartyBurst,
			Decision.ForceRelease);
	protected override bool AllowViceCombo => !FinishNormalCombo && !VPRSyncPlusPolicy.ShouldDelayViceCombo(
		SynchronizePartyBurst, ReawakenReady, PartyBurstActive, SecondsUntilExpectedPartyBurst,
		GCDTime(3) + 1f,
		SwiftTime > Math.Max(SwiftTimer, GCDTime(3) + 1f)
			&& HuntersTime > Math.Max(HuntersTimer, GCDTime(3) + 1f), ShouldHoldFlexibleResources);
	protected override bool AllowUncoiledFury => (!FinishNormalCombo || !HasHostilesInRange)
		&& VPRSyncPlusPolicy.ShouldSpendUncoiledFury(
		ShouldHoldFlexibleResources, PartyBurstActive, RattlingCoilStacks, MaxRattling,
		HasHostilesInRange, TargetDying || (MedicineUncoiledFury && StatusHelper.PlayerHasStatus(true, StatusID.Medicated)));
	protected override bool PreferUncoiledFury => PartyBurstActive;
	protected override bool AllowBurstMedicine => ShouldCommitPersonalBurst && !ReserveBurst;

	protected override bool GeneralGCD(out IAction? act)
	{
		_ = Decision;
		return base.GeneralGCD(out act);
	}

	protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
	{
		_ = Decision;
		// Keep Legacy/Twinfang/Twinblood ahead of potion use.
		// A selected Reawaken has passed CanUse, including its target and action checks.
		if (NoAbilityReady && VPRSyncPlusPolicy.ShouldUseSecondMedicine(
			UseSecondBurstMedicine, EncounterTime, SecondMedicineTime,
			AllowBurstMedicine, AllowReawaken, nextGCD.IsTheSameTo(false, ReawakenPvE))
			&& UseBurstMedicine(out act)) return true;
		return base.EmergencyAbility(nextGCD, out act);
	}

	private static bool IsTrackedBurstAction(ActionID id) => id is ActionID.SerpentsIrePvE
		or ActionID.ReawakenPvE or ActionID.OuroborosPvE or ActionID.UncoiledFuryPvE;
	#endregion

	#region Tracking and Diagnostics
	private void ResetPartyBurstAnchor()
	{
		_lastObservedPartyBurstAt = -1f;
		_observedPartyBurstCycle = 0;
		_lastPartyBuffScanAt = -1f;
		_lastPartyBuffScanVersion = -1;
	}

	public override void OnTerritoryChanged()
	{
		base.OnTerritoryChanged();
		_lastEncounterTime = float.MaxValue;
		_decisionComputedAt = float.MinValue;
	}

	private void UpdateBurstTracker()
	{
		var encounterTime = EncounterTime;
		var elapsed = _lastEncounterTime < 0f ? 0f : Math.Max(0f, encounterTime - _lastEncounterTime);
		if (VPRSyncPlusPolicy.ShouldResetEncounter(_lastEncounterTime, encounterTime, InCombat))
		{
			ReportFight();
			ResetPartyBurstAnchor();
			_lastEncounterTime = -1f;
			_partyBurstStartedAt = -1f;
			_anchorWorthyStartedAt = -1f;
			_burstReadyAt = -1f;
			_lastRotationState = string.Empty;
			_partyBurstWasActive = false;
			_anchorWasWorthy = false;
			_burstWasReady = false;
			_pendingBurstReason = string.Empty;
			_pendingBurstActiveBuffs = 0;
			_pendingBurstExpectedBuffs = 0;
			_lastReportedReasonKey = string.Empty;
			_observedPartyBuffs.Clear();
		}

		_lastEncounterTime = InCombat ? encounterTime : 0f;
		UpdateExpectedPartyBuffs();
		if (!InCombat) return;

		if (_burstWasReady && _decision.Burst == VPRSyncPlusBurstGate.Hold)
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

		var anchorWorthy = IsAnchorWorthy();
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
			&& VPRSyncPlusPolicy.ShouldObservePartyBurst(
				_lastObservedPartyBurstAt, _anchorWorthyStartedAt, PartyBurstCycle))
		{
			_observedPartyBurstCycle = VPRSyncPlusPolicy.AdvancePartyBurstCycle(
				_observedPartyBurstCycle,
				_lastObservedPartyBurstAt,
				_anchorWorthyStartedAt,
				PartyBurstCycle);
			_lastObservedPartyBurstAt = _anchorWorthyStartedAt;
		}

		var burstReady = ReawakenReady && !HasReawakenedActive;
		// A recommendation is not a cast. Retain the budget until entry consumes it.
		_burstReadyAt = VPRSyncPlusPolicy.UpdateHoldStartedAt(_burstReadyAt, encounterTime,
			burstReady, _decision.Burst == VPRSyncPlusBurstGate.Hold);
		_burstWasReady = burstReady;
	}

	private DateTime _lastReportedCastAt;
	private ActionID _lastSeenAction;
	private double _lastSeenActionAge = double.MaxValue;
	private void RecordBurstCasts()
	{
		if (!LogBurstReport) return;
		var age = TimeSinceLastAction.TotalSeconds;
		var changed = _lastSeenAction != DataCenter.LastAction || age < _lastSeenActionAge;
		_lastSeenActionAge = age;
		_lastSeenAction = DataCenter.LastAction;
		if (!changed) return;
		var recordActions = DataCenter.RecordActions;
		for (var i = recordActions.Length - 1; i >= 0; i--)
		{
			var cast = recordActions[i];
			if (cast.UsedTime <= _lastReportedCastAt) continue;
			_lastReportedCastAt = cast.UsedTime;
			if (!IsTrackedBurstAction((ActionID)cast.Action.RowId)) continue;
			_burstUses.Add(new(EncounterTime, _activePartyBuffs, _expectedPartyBuffsCount,
				_decision.Reason, false, DowntimeIn));
			Svc.Log.Info($"[VPR Sync] {EncounterTime:F1}s CONFIRMED {cast.Action.Name}; observed buffs {_activePartyBuffs}/{_expectedPartyBuffsCount}; {_decision.Reason} (buff sample at observation, not damage snapshot)");
		}
	}

	private void ReportFight()
	{
		if (!LogBurstReport || _burstUses.Count == 0)
		{
			_burstUses.Clear();
			_totalHeldSeconds = 0f;
			return;
		}

		Svc.Log.Info($"[VPR Sync] Fight report: {_burstUses.Count} burst uses, {_totalHeldSeconds:F1}s scheduler Hold time.");
		_burstUses.Clear();
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
		var anchorJob = AnchorJob == VPRSyncPlusAnchorJob.Automatic ? null : AnchorJob.ToString();
		var anchorJobActive = false;
		var active = 0;
		var expected = 0;
		var ninjaActive = false;
		var ninjaExpected = false;
		var shortestActiveRemaining = float.MaxValue;
		var names = new List<string>(Buffs.Count);
		var activeJobs = new HashSet<string>();
		var expectedJobs = new HashSet<string>();

		for (var index = 0; index < Buffs.Count; index++)
		{
			var buff = Buffs[index];
			var isActive = IsPartyBuffActive(buff);
			if (InCombat && isActive) _ = _observedPartyBuffs.Add(buff);
			var shouldExpect = VPRSyncPlusPolicy.ShouldExpectPartyBuff(
				encounterTime, _observedPartyBuffs.Contains(buff));
			if (shouldExpect) names.Add($"{buff.JobAbbr} {buff.Name}");

			if (shouldExpect) expectedJobs.Add(buff.JobAbbr);
			if (isActive)
			{
				activeJobs.Add(buff.JobAbbr);
				anchorJobActive |= anchorJob is not null
					&& string.Equals(buff.JobAbbr, anchorJob, StringComparison.OrdinalIgnoreCase);
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

		var expectedNames = string.Join(", ", names);
		if (expectedNames == _expectedPartyBuffs) return;

		_expectedPartyBuffs = expectedNames;
		Svc.Log.Info($"[VPR Sync] Watching party buffs: {(expectedNames.Length == 0 ? "none; personal burst" : expectedNames)}");
	}

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
		return FindDebuffCarrier(buff)?.StatusTime(false, buff.Ids) ?? 0f;
	}

	private bool IsAnchorWorthy()
		=> AnchorJob != VPRSyncPlusAnchorJob.Automatic
			? _anchorJobActive
			: VPRSyncPlusPolicy.IsSignificantPartyBurst(_activeAnchorJobs, _expectedAnchorJobs);

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
				: "first trash burst allowed; Reawaken reserved for boss");
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
		Svc.Log.Info($"[VPR Sync] Dungeon state: {state}");
	}

	private string _lastRotationState = string.Empty;
	private void LogRotationState()
	{
		if (!LogBurstReport) return;

		var state =
			$"burst:[reawaken:{(AllowReawaken ? 1 : 0)} ire:{(ShouldUseSerpentsIre ? 1 : 0)}] "
			+ $"res[bank:{(ShouldHoldFlexibleResources ? 1 : 0)}] "
			+ $"gauge[serpent:{SerpentOffering} anguine:{AnguineTributeStacks} uncoiled:{RattlingCoilStacks}] "
			+ $"plan[{_decision.Burst}/{_decision.Resources}: {VPRSyncPlusPolicy.StableDecisionReason(_decision.Reason)}] "
			+ $"buffs[{_activePartyBuffs}/{_expectedPartyBuffsCount}]";
		// Log individual vetoes only while a ready burst is blocked. Boolean transitions
		// keep this bounded; continuous countdown values would produce a line every frame.
		if (ReawakenReady && !HasReawakenedActive && !AllowReawaken)
			state += $" veto[auto:{!IsBurst} combo:{FinishNormalCombo} swift:{!(SwiftTime > SwiftTimer)}"
				+ $" hunter:{!(HuntersTime > HuntersTimer)} dying:{TargetDying} short:{TargetEndsBeforeBurstPayload}"
				+ $" downtime:{DowntimeWithin(ReawakenExecutionWindow)} commit:{!ShouldCommitPersonalBurst}"
				+ $" pool:{VPRSyncPlusPolicy.ShouldPoolGauge(SynchronizePartyBurst, PartyBurstActive, HasReadyToReawaken, SerpentOffering, SerpentsIrePvE.EnoughLevel && SerpentsIrePvE.IsEnabled && SerpentsIrePvE.Cooldown.ElapsedAfter(ReawakenDelayTimer), SecondsUntilExpectedPartyBurst, Decision.ForceRelease)}"
				+ $" target:{SchedulerTarget?.GameObjectId:X}]";

		if (state == _lastRotationState) return;
		_lastRotationState = state;
		Svc.Log.Info($"[VPR Sync] {EncounterTime:F1}s {state}");
	}
	#endregion

	public override void DisplayRotationStatus()
	{
		base.DisplayRotationStatus();
		var decision = Decision;

		ImGui.Text($"VPR Sync: {decision.Burst} - {decision.Reason}");
		ImGui.Text("Buff timing: Reawaken and Uncoiled Fury; Ire uses its cooldown.");
		ImGui.Text($"Resources: {(decision.Resources == VPRSyncPlusResourcePlan.Bank ? "banking" : "spending")}");
		var plan = SyncPlusBurstPlan switch
		{
			VPRSyncPlusPlan.Automatic => "Automatic (timeline + dungeons)",
			VPRSyncPlusPlan.TrustParty => "Trust party buffs",
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
	}

	private static string Describe(float seconds)
		=> seconds is <= 0f or float.MaxValue ? "unknown" : $"{seconds:F1}s";
}

#region Types
internal enum VPRSyncPlusPlan
{
	[Description("Automatic (timeline + dungeons)")]
	Automatic,

	[Description("Trust party buffs")]
	TrustParty,

	[Description("Solo / ignore party buffs")]
	Personal,
}

internal enum VPRSyncPlusAnchorJob
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

internal enum VPRSyncPlusBurstGate
{
	Free,
	Hold,
	Commit,
}

internal enum VPRSyncPlusResourcePlan
{
	Spend,
	Bank,
}

internal enum VPRSyncPlusDowntimeAction
{
	None,
	Dump,
	HoldCeiling,
}

internal readonly record struct VPRSyncPlusDecision(
	VPRSyncPlusBurstGate Burst,
	VPRSyncPlusResourcePlan Resources,
	string Reason,
	VPRSyncPlusDowntimeAction Downtime = VPRSyncPlusDowntimeAction.None,
	bool ForceRelease = false)
{
	internal static VPRSyncPlusDecision Default
		=> new(VPRSyncPlusBurstGate.Free, VPRSyncPlusResourcePlan.Spend, "starting up");
}

internal readonly record struct VPRSyncPlusBurstUse(
	float At,
	int ActiveBuffs,
	int ExpectedBuffs,
	string Reason,
	bool InVulnerabilityWindow,
	float DowntimeIn);

internal readonly record struct VPRSyncPlusSnapshot(
	bool AutoBurst,
	bool Reserved,
	bool SynchronizeParty,
	bool RequireLiveParty,
	bool BurstReady,
	bool BurstReadySoon,
	float BurstHeldFor,
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
	bool FreeReawaken);
#endregion

internal static class VPRSyncPlusScheduler
{
	internal const string DowntimeDumpReason = "downtime too close for a full window; spending resources";
	internal const string DowntimeHoldCeilingReason = "downtime hold ceiling reached; releasing";

	private static bool Known(float seconds) => seconds is > 0f and < float.MaxValue;

	internal static VPRSyncPlusDecision Evaluate(VPRSyncPlusSnapshot s)
	{
		if (!s.AutoBurst)
		{
			return new(VPRSyncPlusBurstGate.Free, VPRSyncPlusResourcePlan.Spend, "burst is off");
		}
		if (s.Reserved)
		{
			return new(VPRSyncPlusBurstGate.Hold, VPRSyncPlusResourcePlan.Bank, "reserved for the next boss");
		}

		var canHold = s.BurstHeldFor < s.MaximumHold;
		VPRSyncPlusDecision Hold(string reason, VPRSyncPlusResourcePlan resources)
			=> canHold
				? new(VPRSyncPlusBurstGate.Hold, resources, reason)
				: new(VPRSyncPlusBurstGate.Commit, VPRSyncPlusResourcePlan.Spend, "hold ceiling reached", ForceRelease: true);

		// 1. Downtime feasibility
		if (s.TimelineActive && Known(s.DowntimeIn))
		{
			if (s.DowntimeIn < s.PayloadWindow)
			{
				return canHold
					? new(VPRSyncPlusBurstGate.Hold, VPRSyncPlusResourcePlan.Spend, DowntimeDumpReason,
						VPRSyncPlusDowntimeAction.Dump)
					: new(VPRSyncPlusBurstGate.Commit, VPRSyncPlusResourcePlan.Spend,
						DowntimeHoldCeilingReason, VPRSyncPlusDowntimeAction.HoldCeiling, ForceRelease: true);
			}
			if (s.BurstReady && s.DowntimeIn <= s.PayloadWindow + s.LookAhead)
			{
				var betterWindowFits = Known(s.VulnerableIn)
					&& s.VulnerableIn > 0f
					&& s.VulnerableIn <= s.DowntimeIn - s.PayloadWindow;
				var remainingOverlapWait = Math.Max(0f, s.OverlapWait - s.SecondsInPartyBurst);
				var partyWindowFits = s.SynchronizeParty && s.PartyBurstActive
					&& (s.FullPartyBurstActive
						|| s.OverlapWait <= 0f
						|| remainingOverlapWait + s.PayloadWindow <= s.DowntimeIn);
				var predictedWindowFits = s.SynchronizeParty
					&& s.PredictedPartyBurstIn is > 0f and float predicted
					&& predicted + s.PayloadWindow <= s.DowntimeIn;

				if (!betterWindowFits && !partyWindowFits && !predictedWindowFits)
				{
					return new(VPRSyncPlusBurstGate.Commit, VPRSyncPlusResourcePlan.Spend,
						"last full window before downtime", ForceRelease: true);
				}
			}
		}

		// 2. Vulnerability windows
		if (s.TimelineActive)
		{
			var insideWindow = Known(s.VulnerableEndIn)
				&& (!Known(s.VulnerableIn) || s.VulnerableEndIn <= s.VulnerableIn);

			if (insideWindow && s.BurstReady && s.VulnerableEndIn >= s.PayloadWindow * 0.5f)
			{
				return new(VPRSyncPlusBurstGate.Commit, VPRSyncPlusResourcePlan.Spend,
					"vulnerability window open", ForceRelease: true);
			}

			if (!insideWindow && Known(s.VulnerableIn))
			{
				var windowLength = Known(s.VulnerableEndIn)
					? s.VulnerableEndIn - s.VulnerableIn
					: float.MaxValue;
				var windowIsUseful = windowLength >= s.PayloadWindow * 0.5f;
				if (windowIsUseful && s.VulnerableIn <= s.LookAhead)
				{
					if (!s.PartyBurstActive)
					{
						return Hold($"vulnerability window in {s.VulnerableIn:F1}s", VPRSyncPlusResourcePlan.Bank);
					}
				}
			}
		}

		// 3. Raid buff overlap
		if (s.SynchronizeParty && s.PartyBurstActive)
		{
			if (s.FullPartyBurstActive || s.OverlapWait <= 0f || s.SecondsInPartyBurst >= s.OverlapWait)
			{
				return new(VPRSyncPlusBurstGate.Commit, VPRSyncPlusResourcePlan.Spend, "raid buffs are up");
			}
			return Hold("waiting for the rest of the raid buffs", VPRSyncPlusResourcePlan.Bank);
		}

		if (!s.SynchronizeParty)
		{
			return new(VPRSyncPlusBurstGate.Commit, VPRSyncPlusResourcePlan.Spend, "no party sync; personal burst");
		}

		// 4. Fallback prediction
		if (s.RequireLiveParty && s.BurstReadySoon)
		{
			return Hold("waiting for a live raid buff", VPRSyncPlusResourcePlan.Bank);
		}
		if (s.BurstReadySoon && s.PredictedPartyBurstIn.HasValue)
		{
			var predicted = s.PredictedPartyBurstIn.Value;
			// Once Ire supplies a free use, protect it for the known party window.
			// The existing hold ceiling and token-expiry action gate still release it.
			var predictionHorizon = s.FreeReawaken ? Math.Max(s.LookAhead, s.MaximumHold) : s.LookAhead;
			if (predicted <= predictionHorizon && predicted >= -s.MaximumHold)
			{
				return Hold($"raid buffs predicted in {predicted:F1}s", VPRSyncPlusResourcePlan.Bank);
			}
		}

		return new(VPRSyncPlusBurstGate.Commit, VPRSyncPlusResourcePlan.Spend, "no reason to wait", ForceRelease: !canHold);
	}
}

internal static class VPRSyncPlusPolicy
{
	internal static bool ShouldUseSecondMedicine(bool enabled, float now, float earliest,
		bool medicineAllowed, bool reawakenAllowed, bool nextIsReawaken)
		=> enabled && now >= earliest && medicineAllowed && reawakenAllowed && nextIsReawaken;

	internal static float UpdateHoldStartedAt(float startedAt, float now, bool ready, bool holding)
		=> !ready ? -1f : startedAt >= 0f ? startedAt : holding ? now : -1f;
	internal const float PartyBurstRecurrenceTolerance = 30f;

	internal static bool ShouldResetEncounter(float previous, float now, bool inCombat)
		=> previous >= 0f && (now < previous || (!inCombat && previous > 0f));

	internal static bool IsSignificantPartyBurst(int activeJobs, int expectedJobs)
		=> activeJobs > 0 && activeJobs * 2 >= expectedJobs;

	internal static bool ShouldReserve(
		bool autoBurst,
		bool bankDungeonBurst,
		bool protectNextBossBurst,
		bool bossEncounter)
		=> !autoBurst || ((bankDungeonBurst || protectNextBossBurst) && !bossEncounter);

	internal static bool ShouldUseReawaken(
		bool commitBurst,
		bool reawakenReady,
		bool swiftValid,
		bool hunterValid,
		bool targetEndsBeforePayload,
		bool targetAlive,
		bool timelineActive,
		bool downtimeImminent,
		bool gaugeCapped,
		bool freeReawaken)
	{
		if (!reawakenReady || !targetAlive) return false;

		// Buff maintenance check: do not enter Reawaken if Swiftscaled or Hunter's Instinct would drop mid-burst
		if (!swiftValid || !hunterValid) return false;

		// Downtime check: do not start Reawaken if boss jumps in < 9.5s
		if (timelineActive && downtimeImminent) return false;

		if (targetEndsBeforePayload) return false;

		return commitBurst || (gaugeCapped && !freeReawaken);
	}

	internal static bool ShouldUseSerpentsIre(
		bool burstAllowed,
		bool coilCapacity,
		bool ireReady)
		=> burstAllowed && coilCapacity && ireReady;

	internal static bool ShouldPoolGauge(bool synchronize, bool liveBuffs, bool freeReawaken,
		int gauge, bool ireSoon, float? predicted, bool forceRelease)
		=> !forceRelease && synchronize && !liveBuffs && !freeReawaken && gauge < 100
			&& (ireSoon || predicted is >= 0f and <= 45f);

	internal static bool ShouldSpendUncoiledFury(bool banking, bool liveBuffs, int coils,
		int capacity, bool inMelee, bool mustSpend)
		=> !banking || liveBuffs || coils >= capacity || !inMelee || mustSpend;

	internal static float EffectiveOverlapWait(
		float shortestActiveBuffRemaining,
		float payloadWindow,
		float overlapWait)
		=> shortestActiveBuffRemaining <= payloadWindow + 1f ? 0f : overlapWait;

	// A second sequence needs the first Ouroboros recovery GCD as well as its own payload.
	internal static float PlannedBurstWindow(bool freeReawaken, int gauge, float singleWindow, float gcd)
		=> singleWindow + ((freeReawaken && gauge >= 50) || gauge >= 100 ? singleWindow + gcd : 0f);

	internal static bool ShouldFinishNormalCombo(bool pending, float remaining, float burstWindow, float gcd)
		=> pending && remaining > 0f && remaining <= burstWindow + gcd;

	internal static float ComboDeadline(float comboRemaining, float venomRemaining)
		=> venomRemaining > 0f ? Math.Min(comboRemaining, venomRemaining) : comboRemaining;

	internal static bool ShouldDelayViceCombo(bool synchronize, bool burstReady, bool liveBuffs,
		float? predicted, float viceWindow, bool upkeepSafe, bool holding)
		=> synchronize && burstReady && upkeepSafe
			&& (liveBuffs || predicted is float until && until <= viceWindow && (until >= 0f || holding));

	internal static string StableDecisionReason(string reason)
		=> reason.StartsWith("raid buffs predicted in ", StringComparison.Ordinal)
			? "raid buffs predicted"
			: reason.StartsWith("vulnerability window in ", StringComparison.Ordinal)
				? "vulnerability window"
				: reason;

	internal static bool ShouldExpectPartyBuff(float encounterTime, bool observedThisFight)
		=> encounterTime <= 30f || observedThisFight;

	internal static bool ShouldObservePartyBurst(
		float lastObservedAt, float observedAt, float cycle)
	{
		if (lastObservedAt < 0f) return true;
		var elapsed = observedAt - lastObservedAt;
		var cycles = MathF.Round(elapsed / cycle);
		return cycles >= 1f
			&& MathF.Abs(elapsed - cycles * cycle) <= PartyBurstRecurrenceTolerance;
	}

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
}
