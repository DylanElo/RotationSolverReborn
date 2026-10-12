using System.ComponentModel;
using ECommons.DalamudServices;

namespace RotationSolver.RebornRotations.Magical;

/// <summary>
/// Canonical encounter-aware Black Mage rotation.
/// Burst scheduling (Manafont, Ley Lines, Amplifier, and Xenoglossy/Foul) is isolated
/// behind a pure snapshot/decision boundary, matching the MCH Sync+ architecture.
/// </summary>
[Rotation("BLM Sync", CombatType.PvE, GameVersion = "7.55",
	Description = "Encounter-aware Black Mage burst using BossMod timelines when available, with safe raid-buff synchronization, Manafont alignment, Ley Lines scheduling, and cast interruption protection.")]
public sealed class BLM_SyncPlus : BLM_Default
{
	private const float DungeonBossCombatFallback = 90f;
	private const float PartyBurstCycle = BLMSyncPlusPolicy.PartyBurstCycle;
	private const float BurstPayloadWindow = 12f;
	private const float CastInterruptionWindow = 3.0f;
	private const float LeyLinesDowntimeThreshold = 15f;

	#region Config
	[RotationConfig(CombatType.PvE, Name = "Burst strategy",
		Section = "Burst", Order = 100,
		Tooltip = "Automatic uses encounter timelines, predicts future raid-buff windows, and reserves burst for the dungeon boss. Trust party buffs waits for a live raid buff instead of a prediction. Personal ignores party buffs entirely.")]
	private BLMSyncPlusPlan SyncPlusBurstPlan { get; set; } = BLMSyncPlusPlan.Automatic;

	[RotationConfig(CombatType.PvE, Name = "Use BossMod Reborn timeline",
		Section = "Burst", Order = 110,
		Tooltip = "Uses predicted downtime and vulnerability windows when the active BossMod module provides them. Falls back safely when timeline data is unavailable.")]
	private bool UseEncounterTimeline { get; set; } = true;

	[RotationConfig(CombatType.PvE, Name = "Use a second potion",
		Section = "Potions", Order = 210,
		Tooltip = "Allows a later potion when Manafont burst is ready inside the configured timing window.")]
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
	private BLMSyncPlusAnchorJob AnchorJob { get; set; } = BLMSyncPlusAnchorJob.Automatic;

	[RotationConfig(CombatType.PvE, Name = "Enable burst diagnostics", Parent = nameof(AdvancedTiming),
		Section = "Advanced", Order = 420,
		Tooltip = "Logs decision changes during combat and writes a burst summary after the fight. Leave off for normal play.")]
	private bool LogBurstReport { get; set; } = false;

	[Range(0f, 45f, ConfigUnitType.Seconds, 1f)]
	[RotationConfig(CombatType.PvE, Name = "Maximum burst hold", Parent = nameof(AdvancedTiming),
		Section = "Advanced", Order = 430,
		Tooltip = "Hard ceiling for delaying a ready Manafont while waiting for a better window.")]
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
	private BLMSyncPlusDecision _decision = BLMSyncPlusDecision.Default;
	private float _decisionComputedAt = float.MinValue;
	private bool _decisionComputedInCombat;
	private readonly List<BLMSyncPlusBurstUse> _burstUses = [];
	private float _totalHeldSeconds;
	private string _lastReportedReasonKey = string.Empty;
	private string _pendingBurstReason = string.Empty;
	#endregion

	private static float EncounterTime => DataCenter.CombatTimeRaw;
	private bool PartyBurstActive => _activePartyBuffs > 0;
	private bool SignificantPartyBurstActive => PartyBurstActive
		&& BLMSyncPlusPolicy.IsSignificantPartyBurst(_activeAnchorJobs, _expectedAnchorJobs);
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
			return !float.IsNaN(ttk) && ttk < BurstPayloadWindow;
		}
	}

	private bool TargetEndsWithin(float seconds)
	{
		var target = SchedulerTarget;
		var ttk = target?.GetTTK() ?? float.NaN;
		return !float.IsNaN(ttk) && ttk < seconds;
	}

	private bool HasPartyBurstSources
	{
		get
		{
			UpdateExpectedPartyBuffs();
			return _expectedPartyBuffsCount > 0;
		}
	}

	private bool AutomaticDungeon => SyncPlusBurstPlan == BLMSyncPlusPlan.Automatic
		&& DataCenter.Territory?.ContentType == RotationSolver.Basic.Data.TerritoryContentType.Dungeons;
	private bool SynchronizePartyBurst => SyncPlusBurstPlan != BLMSyncPlusPlan.Personal
		&& !AutomaticDungeon && HasPartyBurstSources;
	private bool TimelineEnabled => UseEncounterTimeline;
	private bool TimelineAvailable => TimelineEnabled && BMRActive;
	private float DowntimeIn => TimelineEnabled ? BMRDowntimeIn : float.MaxValue;
	private bool DowntimeWithin(float seconds) => TimelineEnabled && BMRDowntimeWithin(seconds);
	private float VulnerableIn => TimelineEnabled ? BMRVulnerableIn : float.MaxValue;
	private float VulnerableEndIn => TimelineEnabled ? BMRVulnerableEndIn : float.MaxValue;

	private bool ProtectNextDungeonBossBurst => AutomaticDungeon
		&& _dungeonBossCompleted
		&& !_dungeonBossEncounter
		&& !IsCurrentTargetBoss
		&& ManafontPvE.EnoughLevel
		&& ManafontPvE.Cooldown.WillHaveOneCharge(30f);

	private bool ReserveBurst
	{
		get
		{
			UpdateDungeonTracker();
			return BLMSyncPlusPolicy.ShouldReserve(
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

	private bool BurstReady => ManafontPvE.EnoughLevel && !ManafontPvE.Cooldown.IsCoolingDown;
	private bool BurstReadySoon => ManafontPvE.EnoughLevel && (BurstReady || ManafontPvE.Cooldown.WillHaveOneCharge(LookAheadSeconds));

	private BLMSyncPlusDecision Decision
	{
		get
		{
			var now = EncounterTime;
			var inCombat = InCombat;
			if (now == _decisionComputedAt && inCombat == _decisionComputedInCombat) return _decision;

			_decisionComputedAt = now;
			_decisionComputedInCombat = inCombat;

			UpdateBurstTracker();
			var snapshot = new BLMSyncPlusSnapshot(
				AutoBurst: IsBurst,
				Reserved: ReserveBurst,
				SynchronizeParty: SynchronizePartyBurst,
				RequireLiveParty: SyncPlusBurstPlan == BLMSyncPlusPlan.TrustParty,
				BurstReady: BurstReady,
				BurstReadySoon: BurstReadySoon,
				BurstHeldFor: BurstHeldFor,
				TimelineActive: TimelineAvailable,
				DowntimeIn: DowntimeIn,
				VulnerableIn: VulnerableIn,
				VulnerableEndIn: VulnerableEndIn,
				PartyBurstActive: PartyBurstActive,
				FullPartyBurstActive: FullPartyBurstActive,
				SecondsInPartyBurst: SecondsInPartyBurst,
				PredictedPartyBurstIn: SecondsUntilExpectedPartyBurst,
				PayloadWindow: BurstPayloadWindow,
				MaximumHold: MaximumBurstHoldSeconds,
				OverlapWait: Math.Min(OverlapWaitSeconds, _shortestActivePartyBuffRemaining),
				LookAhead: LookAheadSeconds);

			var nextDecision = BLMSyncPlusScheduler.Evaluate(snapshot);
			if (nextDecision != _decision)
			{
				LogDecisionChange(_decision, nextDecision);
				_decision = nextDecision;
			}
			return _decision;
		}
	}

	private bool ShouldUseManafont
		=> BLMSyncPlusPolicy.ShouldUseManafont(Decision.Burst, InAstralFire, CurrentMp, ReserveBurst)
		&& !TargetEndsBeforeBurstPayload;

	private bool ShouldUseLeyLines
		=> BLMSyncPlusPolicy.ShouldUseLeyLines(
			Decision.Burst,
			IsMoving,
			TimelineAvailable && DowntimeWithin(LeyLinesDowntimeThreshold),
			TargetEndsWithin(LeyLinesDowntimeThreshold),
			LeyLinesPvE.Cooldown.CurrentCharges,
			ReserveBurst);

	private bool ShouldUseSecondLeyLinesCharge
		=> Decision.Burst == BLMSyncPlusBurstGate.Commit || Leyline2Madness;

	private bool ShouldSpendPolyglotInBurst
		=> BLMSyncPlusPolicy.ShouldSpendPolyglotInBurst(Decision.Burst, PolyglotStacks,
			!SynchronizePartyBurst || PartyBurstActive
			|| (TimelineAvailable && VulnerableEndIn > 0f && VulnerableEndIn < float.MaxValue
				&& (VulnerableIn <= 0f || VulnerableEndIn <= VulnerableIn)));

	private bool ShouldBankPolyglotForBurst
	{
		get
		{
			var willOvercap = IsPolyglotStacksMaxed && (EnochianEndAfterGCD(3) || AmplifierPvE.Cooldown.WillHaveOneChargeGCD(3));
			return BLMSyncPlusPolicy.ShouldBankPolyglot(Decision.Burst, Decision.Resources, PolyglotStacks, IsPolyglotStacksMaxed, willOvercap);
		}
	}

	private bool ShouldDumpPolyglotBeforeDowntime
		=> BLMSyncPlusPolicy.ShouldDumpPolyglotBeforeDowntime(Decision.Downtime, DowntimeIn, CastInterruptionWindow, PolyglotStacks);

	private bool ShouldUseBurstMedicine
		=> BLMSyncPlusPolicy.ShouldUseOpenerMedicine(EncounterTime, HasBuffs, ReserveBurst)
		|| BLMSyncPlusPolicy.ShouldUseSecondMedicine(UseSecondBurstMedicine, SecondMedicineTime, EncounterTime, Decision.Burst);

	// Sync owns Ley Lines scheduling; the parent must not retry a rejected cast.
	protected override bool AllowAutomaticLeyLines => false;

	#region Additional oGCD Logic
	[RotationDesc]
	protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
	{
		_ = Decision;

		if (UseRetrace && RetracePvE.CanUse(out act))
		{
			return true;
		}

		// To Fire
		if (CurrentMp >= 7200 && UmbralIceStacks == 2 && ParadoxPvE.EnoughLevel)
		{
			if ((HasFire || HasSwift) && TransposePvE.CanUse(out act))
			{
				return true;
			}
		}
		if (nextGCD.IsTheSameTo(false, FireIiiPvE) && HasFire)
		{
			if (TransposePvE.CanUse(out act))
			{
				return true;
			}
		}

		// Manafont commitment
		if (InAstralFire)
		{
			if (ShouldUseManafont && CurrentMp == 0 && ManafontPvE.CanUse(out act))
			{
				RecordBurstUse("Manafont burst commit");
				return true;
			}

			// If Manafont is not ready or being held, allow transpose to Ice if needed
			if (NeedToTransposeGoIce(true) && TransposePvE.CanUse(out act))
			{
				return true;
			}
		}

		act = null;
		return false;
	}

	[RotationDesc(ActionID.TransposePvE, ActionID.LeyLinesPvE, ActionID.RetracePvE)]
	protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
	{
		_ = Decision;

		if (IsMoving && HasHostilesInRange && InCombat && TriplecastPvE.CanUse(out act, usedUp: true))
		{
			return true;
		}

		if (InCombat && HasHostilesInRange && !IsMoving && LeyLinesPvE.EnoughLevel)
		{
			if (ShouldUseLeyLines && LeyLinesPvE.CanUse(out act, usedUp: ShouldUseSecondLeyLinesCharge))
			{
				return true;
			}
		}

		if (!IsLastAbility(ActionID.LeyLinesPvE) && UseRetrace && InCombat && HasHostilesInRange && RetracePvE.CanUse(out act))
		{
			return true;
		}

		return base.GeneralAbility(nextGCD, out act);
	}

	[RotationDesc(ActionID.RetracePvE, ActionID.SwiftcastPvE, ActionID.TriplecastPvE, ActionID.AmplifierPvE)]
	protected override bool AttackAbility(IAction nextGCD, out IAction? act)
	{
		_ = Decision;

		if (InUmbralIce)
		{
			if (UmbralIceStacks == 2 && !HasFire && !IsLastGCD(ActionID.ParadoxPvE))
			{
				if (SwiftcastPvE.CanUse(out act)) return true;
				if (TriplecastPvE.CanUse(out act, usedUp: true)) return true;
			}

			if (UmbralIceStacks < MaxSoulCount && LucidDreamingPvE.CanUse(out act))
			{
				return true;
			}
		}

		if (InAstralFire)
		{
			if (TriplecastPvE.CanUse(out act, gcdCountForAbility: 5))
			{
				return true;
			}
		}

		// Amplifier management: avoid overcapping Polyglot
		if (!IsPolyglotStacksMaxed && AmplifierPvE.CanUse(out act))
		{
			return true;
		}

		if (TriplecastPvE.CanUse(out act))
		{
			if (nextGCD.Equals(ActionID.OccultCometPvE))
			{
				return true;
			}
		}

		if (ShouldUseBurstMedicine && UseBurstMedicine(out act))
		{
			return true;
		}

		act = null;
		return false;
	}
	#endregion

	#region GCD Logic
	protected override bool GeneralGCD(out IAction? act)
	{
		_ = Decision;

		// 1. Downtime Cast Protection:
		// If BossMod reports downtime within 3.0s, hard-casting Fire IV (2.8s) or Despair (3.0s) will be interrupted!
		// Suppress long hard-casts if !NextGCDisInstant, and instead prioritize instant casts (Xenoglossy, Paradox, High Thunder, Scathe).
		var suppressHardCast = BLMSyncPlusPolicy.ShouldSuppressHardCastForDowntime(DowntimeIn, CastInterruptionWindow, NextGCDisInstant);
		if (TimelineAvailable && suppressHardCast)
		{
			if (FoulPvE.CanUse(out act, skipAoeCheck: !XenoglossyPvE.EnoughLevel)) return true;
			if (XenoglossyPvE.CanUse(out act)) return true;
			if (IsParadoxActive && ParadoxPvE.EnoughLevel && BlizzardPvE.CanUse(out act)) return true;
			if (HasThunder && ThunderIiiPvE.CanUse(out act)) return true;
			if (HasThunder && ThunderPvE.CanUse(out act)) return true;
			if (ScathePvE.CanUse(out act)) return true;

			act = null;
			return false;
		}

		// 2. High-priority Flare Star if ready (Astral Soul == 6)
		if (FlareStarPvE.CanUse(out act))
		{
			return true;
		}

		return base.GeneralGCD(out act);
	}

	protected override bool UsePolyglot(out IAction? act, uint gcdCount = 3)
	{
		act = null;

		// 1. Imminent downtime dump: spend all available polyglot before boss leaves
		if (ShouldDumpPolyglotBeforeDowntime && PolyglotStacks > 0)
		{
			if (FoulPvE.CanUse(out act, skipAoeCheck: !XenoglossyPvE.EnoughLevel)) return true;
			if (XenoglossyPvE.CanUse(out act)) return true;
		}

		// 2. Burst commit: spend Xenoglossy in party raid buffs / vulnerability window
		if (ShouldSpendPolyglotInBurst && PolyglotStacks > 0)
		{
			if (FoulPvE.CanUse(out act, skipAoeCheck: !XenoglossyPvE.EnoughLevel)) return true;
			if (XenoglossyPvE.CanUse(out act)) return true;
		}

		// 3. Overcap protection: if max stacks and Enochian tick or Amplifier is imminent
		if (IsPolyglotStacksMaxed && (EnochianEndAfterGCD(gcdCount) || AmplifierPvE.Cooldown.WillHaveOneChargeGCD(gcdCount)))
		{
			if (FoulPvE.CanUse(out act, skipAoeCheck: !XenoglossyPvE.EnoughLevel)) return true;
			if (XenoglossyPvE.CanUse(out act)) return true;
		}

		// 4. Movement / weave fallback if requested (gcdCount == 0) and not strictly holding for burst
		if (gcdCount == 0 && (!ShouldBankPolyglotForBurst || PolyglotStacks > 1))
		{
			if (FoulPvE.CanUse(out act, skipAoeCheck: !XenoglossyPvE.EnoughLevel)) return true;
			if (XenoglossyPvE.CanUse(out act)) return true;
		}

		return false;
	}

	protected override bool GoIce(out IAction? act)
	{
		act = null;

		if (!NeedToGoIce)
		{
			return false;
		}

		// Use Manafont or transpose if burst is ready/allowed
		if (ShouldUseManafont && (!ManafontPvE.Cooldown.IsCoolingDown || NeedToTransposeGoIce(false))
			&& UseInstanceSpell(out act))
		{
			return true;
		}

		// Go to Ice directly
		if (BlizzardIiPvE.CanUse(out act)) return true;
		if (BlizzardIiiPvE.CanUse(out act)) return true;
		if (TransposePvE.CanUse(out act)) return true;
		if (BlizzardPvE.CanUse(out act)) return true;

		return false;
	}
	#endregion

	#region Tracking and Diagnostics
	private void ResetEncounterTracking()
	{
		_lastObservedPartyBurstAt = -1f;
		_lastEncounterTime = -1f;
		_partyBurstStartedAt = -1f;
		_anchorWorthyStartedAt = -1f;
		_burstReadyAt = -1f;
		_partyBurstWasActive = false;
		_anchorWasWorthy = false;
		_burstWasReady = false;
		_pendingBurstReason = string.Empty;
		_lastReportedReasonKey = string.Empty;
		_observedPartyBuffs.Clear();
		_lastPartyBuffScanAt = -1f;
		_decision = BLMSyncPlusDecision.Default;
	}

	public override void OnTerritoryChanged()
	{
		base.OnTerritoryChanged();
		ReportFight();
		ResetEncounterTracking();
		_decisionComputedAt = float.MinValue;
	}

	private void UpdateBurstTracker()
	{
		var encounterTime = EncounterTime;
		var elapsed = _lastEncounterTime < 0f ? 0f : Math.Max(0f, encounterTime - _lastEncounterTime);
		if (BLMSyncPlusPolicy.ShouldResetEncounter(_lastEncounterTime, encounterTime, InCombat))
		{
			ReportFight();
			ResetEncounterTracking();
		}

		_lastEncounterTime = InCombat ? encounterTime : -1f;
		UpdateExpectedPartyBuffs();
		if (!InCombat) return;

		if (_burstWasReady && _decision.Burst == BLMSyncPlusBurstGate.Hold)
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

		if (anchorWorthy && _anchorWorthyStartedAt >= 0f)
		{
			var currentCycle = (int)MathF.Floor(encounterTime / PartyBurstCycle);
			if (BLMSyncPlusPolicy.ShouldObservePartyBurst(_lastObservedPartyBurstAt, _anchorWorthyStartedAt))
			{
				_lastObservedPartyBurstAt = _anchorWorthyStartedAt;
				if (LogBurstReport)
				{
					Svc.Log.Info($"[BLM Sync] Learned party burst anchor at {encounterTime:F1}s (cycle {currentCycle})");
				}
			}
		}
		_anchorWasWorthy = anchorWorthy;

		var burstReady = BurstReady;
		if (burstReady && !_burstWasReady)
		{
			_burstReadyAt = encounterTime;
		}
		else if (!burstReady)
		{
			_burstReadyAt = -1f;
		}
		_burstWasReady = burstReady;
	}

	private void ReportFight()
	{
		if (!LogBurstReport || _burstUses.Count == 0)
		{
			_burstUses.Clear();
			_totalHeldSeconds = 0f;
			return;
		}

		Svc.Log.Info($"[BLM Sync] Fight report: {_burstUses.Count} burst uses, {_totalHeldSeconds:F1}s scheduler Hold time.");
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
		var anchorJob = AnchorJob == BLMSyncPlusAnchorJob.Automatic ? null : AnchorJob.ToString();
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
			var shouldExpect = BLMSyncPlusPolicy.ShouldExpectPartyBuff(
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
		Svc.Log.Info($"[BLM Sync] Watching party buffs: {(expectedNames.Length == 0 ? "none; personal burst" : expectedNames)}");
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
		=> AnchorJob != BLMSyncPlusAnchorJob.Automatic
			? _anchorJobActive
			: BLMSyncPlusPolicy.IsSignificantPartyBurst(_activeAnchorJobs, _expectedAnchorJobs);

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
				: "first trash burst allowed; Manafont reserved for boss");
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

	private void LogDungeonState(string message)
	{
		if (_lastDungeonLogState == message) return;
		_lastDungeonLogState = message;
		Svc.Log.Info($"[BLM Sync] Dungeon: {message}");
	}

	private void LogDecisionChange(BLMSyncPlusDecision from, BLMSyncPlusDecision to)
	{
		if (!LogBurstReport || !InCombat) return;

		var key = ExtractReasonKey(to.Reason);
		if (key == _lastReportedReasonKey && from.Burst == to.Burst && from.Resources == to.Resources) return;

		_lastReportedReasonKey = key;
		Svc.Log.Info($"[BLM Sync] [{EncounterTime:F1}s] {from.Burst}/{from.Resources} -> {to.Burst}/{to.Resources} ({to.Reason}) [Active: {_activePartyBuffs}/{_expectedPartyBuffsCount}]");
	}

	private static string ExtractReasonKey(string reason)
	{
		var paren = reason.IndexOf('(');
		if (paren > 0) return reason[..paren].Trim();
		var inIdx = reason.IndexOf(" in ", StringComparison.Ordinal);
		return inIdx > 0 ? reason[..inIdx].Trim() : reason;
	}

	private void RecordBurstUse(string context)
	{
		var use = new BLMSyncPlusBurstUse(
			At: EncounterTime,
			ActiveBuffs: _activePartyBuffs,
			ExpectedBuffs: _expectedPartyBuffsCount,
			Reason: string.IsNullOrEmpty(_pendingBurstReason) ? context : $"{context} ({_pendingBurstReason})",
			InVulnerabilityWindow: TimelineAvailable && VulnerableEndIn > 0f && VulnerableEndIn < float.MaxValue,
			DowntimeIn: DowntimeIn);
		_burstUses.Add(use);

		if (LogBurstReport)
		{
			Svc.Log.Info($"[BLM Sync] Burst commit at {use.At:F1}s: {use.Reason} (Buffs: {use.ActiveBuffs}/{use.ExpectedBuffs})");
		}

		_pendingBurstReason = string.Empty;
	}
	#endregion
}

#region Types
internal enum BLMSyncPlusPlan
{
	[Description("Automatic (timeline + dungeons)")]
	Automatic,

	[Description("Trust party buffs")]
	TrustParty,

	[Description("Solo / ignore party buffs")]
	Personal,
}

internal enum BLMSyncPlusAnchorJob
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

internal enum BLMSyncPlusBurstGate
{
	Free,
	Hold,
	Commit,
}

internal enum BLMSyncPlusResourcePlan
{
	Spend,
	Bank,
}

internal enum BLMSyncPlusDowntimeAction
{
	None,
	Dump,
	HoldCeiling,
}

internal readonly record struct BLMSyncPlusDecision(
	BLMSyncPlusBurstGate Burst,
	BLMSyncPlusResourcePlan Resources,
	string Reason,
	BLMSyncPlusDowntimeAction Downtime = BLMSyncPlusDowntimeAction.None)
{
	internal static BLMSyncPlusDecision Default
		=> new(BLMSyncPlusBurstGate.Free, BLMSyncPlusResourcePlan.Spend, "starting up");
}

internal readonly record struct BLMSyncPlusBurstUse(
	float At,
	int ActiveBuffs,
	int ExpectedBuffs,
	string Reason,
	bool InVulnerabilityWindow,
	float DowntimeIn);

internal readonly record struct BLMSyncPlusSnapshot(
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
	float LookAhead);
#endregion

internal static class BLMSyncPlusScheduler
{
	internal const string DowntimeDumpReason = "downtime too close for a full window; spending resources";
	internal const string DowntimeHoldCeilingReason = "downtime hold ceiling reached; releasing";

	private static bool Known(float seconds) => seconds is > 0f and < float.MaxValue;

	internal static BLMSyncPlusDecision Evaluate(BLMSyncPlusSnapshot s)
	{
		if (!s.AutoBurst)
		{
			return new(BLMSyncPlusBurstGate.Free, BLMSyncPlusResourcePlan.Spend, "burst is off");
		}
		if (s.Reserved)
		{
			return new(BLMSyncPlusBurstGate.Hold, BLMSyncPlusResourcePlan.Bank, "reserved for the next boss");
		}

		var canHold = s.BurstHeldFor < s.MaximumHold;
		BLMSyncPlusDecision Hold(string reason, BLMSyncPlusResourcePlan resources)
			=> canHold
				? new(BLMSyncPlusBurstGate.Hold, resources, reason)
				: new(BLMSyncPlusBurstGate.Commit, BLMSyncPlusResourcePlan.Spend, "hold ceiling reached");

		// 1. Downtime feasibility
		if (s.TimelineActive && Known(s.DowntimeIn))
		{
			if (s.DowntimeIn < s.PayloadWindow)
			{
				return canHold
					? new(BLMSyncPlusBurstGate.Hold, BLMSyncPlusResourcePlan.Spend, DowntimeDumpReason,
						BLMSyncPlusDowntimeAction.Dump)
					: new(BLMSyncPlusBurstGate.Commit, BLMSyncPlusResourcePlan.Spend,
						DowntimeHoldCeilingReason, BLMSyncPlusDowntimeAction.HoldCeiling);
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
					return new(BLMSyncPlusBurstGate.Commit, BLMSyncPlusResourcePlan.Spend,
						"last full window before downtime");
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
				return new(BLMSyncPlusBurstGate.Commit, BLMSyncPlusResourcePlan.Spend,
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
					if (!s.PartyBurstActive)
					{
						return Hold($"vulnerability window in {s.VulnerableIn:F1}s", BLMSyncPlusResourcePlan.Bank);
					}
				}
			}
		}

		// 3. Raid buff overlap
		if (s.SynchronizeParty && s.PartyBurstActive)
		{
			if (s.FullPartyBurstActive || s.OverlapWait <= 0f || s.SecondsInPartyBurst >= s.OverlapWait)
			{
				return new(BLMSyncPlusBurstGate.Commit, BLMSyncPlusResourcePlan.Spend, "raid buffs are up");
			}
			return Hold("waiting for the rest of the raid buffs", BLMSyncPlusResourcePlan.Bank);
		}

		if (!s.SynchronizeParty)
		{
			return new(BLMSyncPlusBurstGate.Commit, BLMSyncPlusResourcePlan.Spend, "no party sync; personal burst");
		}

		// 4. Fallback prediction
		if (s.RequireLiveParty && s.BurstReadySoon)
		{
			return Hold("waiting for a live raid buff", BLMSyncPlusResourcePlan.Bank);
		}
		if (s.BurstReadySoon && s.PredictedPartyBurstIn.HasValue)
		{
			var predicted = s.PredictedPartyBurstIn.Value;
			if (predicted <= s.LookAhead && predicted >= -s.MaximumHold)
			{
				return Hold($"raid buffs predicted in {predicted:F1}s", BLMSyncPlusResourcePlan.Bank);
			}
		}

		return new(BLMSyncPlusBurstGate.Commit, BLMSyncPlusResourcePlan.Spend, "no reason to wait");
	}
}

internal static class BLMSyncPlusPolicy
{
	internal const float PartyBurstCycle = 120f;
	internal const float PartyBurstRecurrenceTolerance = 30f;

	internal static bool ShouldResetEncounter(float previousTime, float currentTime, bool inCombat)
		=> previousTime >= 0f && (!inCombat || currentTime < previousTime);

	internal static bool ShouldObservePartyBurst(float lastObserved, float candidate)
		=> candidate >= 0f && (lastObserved < 0f || candidate - lastObserved >= PartyBurstCycle - PartyBurstRecurrenceTolerance);

	internal static bool IsSignificantPartyBurst(int activeJobs, int expectedJobs)
		=> activeJobs > 0 && activeJobs * 2 >= expectedJobs;

	internal static bool ShouldReserve(
		bool autoBurst,
		bool bankDungeonBurst,
		bool protectNextBossBurst,
		bool bossEncounter)
		=> !autoBurst || ((bankDungeonBurst || protectNextBossBurst) && !bossEncounter);

	internal static bool ShouldUseManafont(
		BLMSyncPlusBurstGate burstGate,
		bool inAstralFire,
		uint currentMp,
		bool isReserved)
	{
		if (isReserved || !inAstralFire) return false;
		if (burstGate == BLMSyncPlusBurstGate.Hold) return false;
		return currentMp == 0;
	}

	internal static bool ShouldUseLeyLines(
		BLMSyncPlusBurstGate burstGate,
		bool isMoving,
		bool downtimeImminent,
		bool targetDying,
		ushort currentCharges,
		bool isReserved)
	{
		if (isMoving || downtimeImminent || targetDying || isReserved) return false;
		if (burstGate == BLMSyncPlusBurstGate.Commit || burstGate == BLMSyncPlusBurstGate.Free) return true;
		if (burstGate == BLMSyncPlusBurstGate.Hold)
		{
			// Prevent overcapping when at 2 charges even during hold
			return currentCharges >= 2;
		}
		return false;
	}

	internal static bool ShouldSpendPolyglotInBurst(
		BLMSyncPlusBurstGate burstGate,
		byte polyglotStacks,
		bool spendWindow)
		=> burstGate == BLMSyncPlusBurstGate.Commit && spendWindow && polyglotStacks > 0;

	internal static bool ShouldBankPolyglot(
		BLMSyncPlusBurstGate burstGate,
		BLMSyncPlusResourcePlan resourcePlan,
		byte polyglotStacks,
		bool isMaxStacks,
		bool willOvercapSoon)
	{
		if (willOvercapSoon) return false;
		return burstGate == BLMSyncPlusBurstGate.Hold || resourcePlan == BLMSyncPlusResourcePlan.Bank;
	}

	internal static bool ShouldDumpPolyglotBeforeDowntime(
		BLMSyncPlusDowntimeAction downtimeAction,
		float downtimeIn,
		float downtimeThreshold,
		byte polyglotStacks)
	{
		if (polyglotStacks == 0) return false;
		return downtimeAction == BLMSyncPlusDowntimeAction.Dump
			|| (downtimeIn > 0f && downtimeIn <= downtimeThreshold);
	}

	internal static bool ShouldSuppressHardCastForDowntime(
		float downtimeIn,
		float castTimeThreshold,
		bool isInstantGCD)
	{
		if (isInstantGCD) return false;
		return downtimeIn > 0f && downtimeIn <= castTimeThreshold;
	}

	internal static bool ShouldExpectPartyBuff(float encounterTime, bool observedActive)
		=> observedActive || encounterTime < PartyBurstCycle - PartyBurstRecurrenceTolerance;

	internal static bool ShouldUseSecondMedicine(
		bool enabled,
		float configuredTime,
		float encounterTime,
		BLMSyncPlusBurstGate burstGate)
		=> enabled && burstGate == BLMSyncPlusBurstGate.Commit && encounterTime >= configuredTime;

	internal static bool ShouldUseOpenerMedicine(float encounterTime, bool hasBuffs, bool reserved)
		=> encounterTime >= 0f && encounterTime < 30f && hasBuffs && !reserved;
}
