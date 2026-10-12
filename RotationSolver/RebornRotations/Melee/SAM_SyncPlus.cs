using System.ComponentModel;
using ECommons.DalamudServices;

namespace RotationSolver.RebornRotations.Melee;

/// <summary>
/// Canonical encounter-aware Samurai rotation.
/// Burst scheduling (Ikishoten, Zanshin, Ogi Namikiri, Senei, and Meikyo Shisui) is isolated
/// behind a pure snapshot/decision boundary, matching the MCH Sync+ architecture.
/// </summary>
[Rotation("SAM Sync", CombatType.PvE, GameVersion = "7.55",
	Description = "Encounter-aware Samurai burst using BossMod timelines when available, with safe raid-buff synchronization and Kenki/Meikyo banking.")]
public sealed class SAM_SyncPlus : SAM_Reborn
{
	private const float DungeonBossCombatFallback = 90f;
	private const float PartyBurstCycle = 120f;
	private const float OgiCastTimeWindow = 2.0f;
	private const float IaijutsuCastTimeWindow = 2.0f;
	private const float BurstPayloadWindow = 12f;

	#region Config
	[RotationConfig(CombatType.PvE, Name = "Burst strategy",
		Section = "Burst", Order = 100,
		Tooltip = "Automatic uses encounter timelines, predicts future raid-buff windows, and reserves burst for the dungeon boss. Trust party buffs waits for a live raid buff instead of a prediction. Personal ignores party buffs entirely.")]
	private SAMSyncPlusPlan SyncPlusBurstPlan { get; set; } = SAMSyncPlusPlan.Automatic;

	[RotationConfig(CombatType.PvE, Name = "Use BossMod Reborn timeline",
		Section = "Burst", Order = 110,
		Tooltip = "Uses predicted downtime and vulnerability windows when the active BossMod module provides them. Falls back safely when timeline data is unavailable.")]
	private bool UseEncounterTimeline { get; set; } = true;

	[RotationConfig(CombatType.PvE, Name = "Use a second potion",
		Section = "Potions", Order = 210,
		Tooltip = "Allows a later potion when Ikishoten burst is ready inside the configured timing window.")]
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
	private SAMSyncPlusAnchorJob AnchorJob { get; set; } = SAMSyncPlusAnchorJob.Automatic;

	[RotationConfig(CombatType.PvE, Name = "Enable burst diagnostics", Parent = nameof(AdvancedTiming),
		Section = "Advanced", Order = 420,
		Tooltip = "Logs decision changes during combat and writes a burst summary after the fight. Records confirmed burst casts and decision changes for troubleshooting.")]
	private bool LogBurstReport { get; set; } = true;

	[Range(0f, 45f, ConfigUnitType.Seconds, 1f)]
	[RotationConfig(CombatType.PvE, Name = "Maximum burst hold", Parent = nameof(AdvancedTiming),
		Section = "Advanced", Order = 430,
		Tooltip = "Hard ceiling for delaying a ready Ikishoten while waiting for a better window.")]
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
	private float _ikishotenReadyAt = -1f;
	private float _lastEncounterTime = -1f;
	private bool _partyBurstWasActive;
	private bool _anchorWasWorthy;
	private bool _ikishotenWasReady;
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
	private SAMSyncPlusDecision _decision = SAMSyncPlusDecision.Default;
	private float _decisionComputedAt = float.MinValue;
	private bool _decisionComputedInCombat;
	private readonly List<SAMSyncPlusBurstUse> _burstUses = [];
	private float _totalHeldSeconds;
	private string _lastReportedReasonKey = string.Empty;
	private string _pendingBurstReason = string.Empty;
	private int _pendingBurstActiveBuffs;
	private int _pendingBurstExpectedBuffs;
	#endregion

	private static float EncounterTime => DataCenter.CombatTimeRaw;
	private bool PartyBurstActive => _activePartyBuffs > 0;
	private bool SignificantPartyBurstActive => PartyBurstActive
		&& SAMSyncPlusPolicy.IsSignificantPartyBurst(_activeAnchorJobs, _expectedAnchorJobs);
	private bool FullPartyBurstActive => _expectedPartyBuffsCount > 0
		&& _activePartyBuffs >= _expectedPartyBuffsCount;
	private float SecondsInPartyBurst => _partyBurstStartedAt < 0f ? 0f : EncounterTime - _partyBurstStartedAt;
	private float IkishotenHeldFor => _ikishotenReadyAt < 0f ? 0f : EncounterTime - _ikishotenReadyAt;
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

	private bool HasPartyBurstSources
	{
		get
		{
			UpdateExpectedPartyBuffs();
			return _expectedPartyBuffsCount > 0;
		}
	}

	private bool AutomaticDungeon => SyncPlusBurstPlan == SAMSyncPlusPlan.Automatic
		&& DataCenter.Territory?.ContentType == RotationSolver.Basic.Data.TerritoryContentType.Dungeons;
	private bool SynchronizePartyBurst => SyncPlusBurstPlan != SAMSyncPlusPlan.Personal
		&& !AutomaticDungeon && HasPartyBurstSources;
	private bool TimelineEnabled => UseEncounterTimeline;
	private bool TimelineAvailable => TimelineEnabled && BMRActive;
	private float DowntimeIn => TimelineEnabled ? BMRDowntimeIn : float.MaxValue;
	private bool DowntimeWithin(float seconds) => TimelineEnabled && BMRDowntimeWithin(seconds);

	private bool ProtectNextDungeonBossBurst => AutomaticDungeon
		&& _dungeonBossCompleted
		&& !_dungeonBossEncounter
		&& !IsCurrentTargetBoss
		&& IkishotenPvE.EnoughLevel
		&& IkishotenPvE.Cooldown.WillHaveOneCharge(30f);

	private bool ReserveBurst
	{
		get
		{
			UpdateDungeonTracker();
			return SAMSyncPlusPolicy.ShouldReserve(
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

	private SAMSyncPlusDecision Decision
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

			_decision = SAMSyncPlusScheduler.Evaluate(new SAMSyncPlusSnapshot(
				IsBurst,
				ReserveBurst,
				SynchronizePartyBurst,
				SyncPlusBurstPlan == SAMSyncPlusPlan.TrustParty,
				IkishotenPvE.Cooldown.HasOneCharge,
				IkishotenPvE.Cooldown.WillHaveOneCharge(LookAheadSeconds),
				IkishotenHeldFor,
				TimelineAvailable,
				DowntimeIn,
				TimelineEnabled ? BMRVulnerableIn : float.MaxValue,
				TimelineEnabled ? BMRVulnerableEndIn : float.MaxValue,
				PartyBurstActive,
				FullPartyBurstActive,
				SecondsInPartyBurst,
				SecondsUntilExpectedPartyBurst,
				BurstPayloadWindow,
				MaximumBurstHoldSeconds,
				SAMSyncPlusPolicy.EffectiveOverlapWait(
					_shortestActivePartyBuffRemaining,
					BurstPayloadWindow,
					OverlapWaitSeconds),
				LookAheadSeconds));

			if (_decision.Burst == SAMSyncPlusBurstGate.Commit && IkishotenPvE.Cooldown.HasOneCharge)
			{
				_pendingBurstReason = _decision.Reason;
				_pendingBurstActiveBuffs = _activePartyBuffs;
				_pendingBurstExpectedBuffs = _expectedPartyBuffsCount;
			}

			var reasonKey = SAMSyncPlusPolicy.StableDecisionReason(_decision.Reason);
			if (LogBurstReport && reasonKey != _lastReportedReasonKey && InCombat)
			{
				_lastReportedReasonKey = reasonKey;
				Svc.Log.Info($"[SAM Sync] {now:F1}s {_decision.Burst}/{_decision.Resources}: {_decision.Reason} (buffs {_activePartyBuffs}/{_expectedPartyBuffsCount}, Kenki {Kenki}, Sen {SenCount})");
			}

			RecordBurstCasts();
			LogRotationState();
			return _decision;
		}
	}

	#region Burst & Action Hooks
	private bool ShouldCommitPersonalBurst => Decision.Burst == SAMSyncPlusBurstGate.Commit;
	private bool ShouldHoldFlexibleResources => Decision.Resources == SAMSyncPlusResourcePlan.Bank;

	private bool ShouldUseIkishoten
		=> SAMSyncPlusPolicy.ShouldUseIkishoten(
			ShouldCommitPersonalBurst,
			Kenki <= 50,
			TargetEndsBeforeBurstPayload,
			!TargetDying);

	private bool ShouldHoldMeikyoForBurst
		=> MeikyoShisuiPvE.Cooldown.HasOneCharge && !HasMeikyoShisui && !HasTendo
			&& !SAMSyncCycle.UseMeikyo(CycleState);

	private bool ShouldBankKenkiForBurst
		=> SAMSyncPlusPolicy.ShouldBankKenki(
			ShouldHoldFlexibleResources,
			Kenki,
			85);

	private bool ShouldDumpKenkiBeforeDowntime
		=> SAMSyncPlusPolicy.ShouldDumpKenkiBeforeDowntime(
			TimelineAvailable,
			DowntimeIn,
			Kenki);

	private bool ShouldUseBurstMedicine
		=> SAMSyncPlusPolicy.ShouldUseSecondMedicine(UseSecondBurstMedicine, SecondMedicineTime, EncounterTime, Decision.Burst);
	protected override bool AllowBurstMedicine => SAMSyncPlusPolicy.AllowMedicine(
		ShouldCommitPersonalBurst, EncounterTime, UseSecondBurstMedicine, SecondMedicineTime);

	protected override bool CanUseReadyAction(IBaseAction action, out IAction? act,
		bool skipComboCheck = false, bool skipAoeCheck = false)
	{
		act = null;
		// Required procs need to survive execution, not a DoT refresh horizon.
		if (action.Setting.StatusNeed is { Length: > 0 } needed
			&& !SAMSyncCycle.RequiredStatusCovers(
				StatusHelper.PlayerStatusTime(action.Setting.StatusFromSelf, needed),
				action.Info.IsRealGCD ? DataCenter.DefaultGCDRemain : 0f, action.Info.CastTime)) return false;
		return action.CanUse(out act, skipStatusNeed: true,
			skipComboCheck: skipComboCheck, skipAoeCheck: skipAoeCheck);
	}

	protected override bool AttackAbility(IAction nextGCD, out IAction? act)
	{
		_ = Decision;

		// Spend capped Meditation before another Iaijutsu, including during Ogi/Zanshin readiness.
		if (ShohaPvE.CanUse(out act)) return true;
		if (HasHostilesInRange && !TendoKaeshiSetsugekkaReady && SAMSyncCycle.UseMeikyo(CycleState)
			&& MeikyoShisuiPvE.CanUse(out act, usedUp: true)) return true;

		// Consume a ready payload before spending its Kenki on the downtime dump.
		if ((ShouldDumpKenkiBeforeDowntime || ShouldSpendPayload(StatusHelper.PlayerStatusTime(true, StatusID.ZanshinReady_3855)))
			&& CanUseReadyAction(ZanshinPvE, out act))
		{
			return true;
		}

		// 3. Ikishoten commitment: aligned with raid buffs and Kenki <= 50
		if (ShouldUseIkishoten && !CombatElapsedLessGCD(2) && IkishotenPvE.CanUse(out act))
		{
			return true;
		}

		// 4. Senei / Guren retain their 60s cadence; only hold near an actual/predicted buff window.
		if (SAMSyncPlusPolicy.ShouldUseSenei(IkishotenPvE.Cooldown.IsCoolingDown,
			TargetDying || TargetEndsBeforeBurstPayload, ShouldDumpKenkiBeforeDowntime,
			ShouldHoldFlexibleResources, Kenki, HasZanshinReady))
		{
			if (HissatsuGurenPvE.CanUse(out act, skipAoeCheck: !HissatsuSeneiPvE.EnoughLevel))
			{
				return true;
			}
			if (HissatsuSeneiPvE.CanUse(out act))
			{
				return true;
			}
		}

		// 7. Kenki Spenders (Shinten / Kyuten):
		// Respect banking for burst (pool up to 85 Kenki before the window), but dump if capping or in burst
		if (!HasZanshinReady || ShouldDumpKenkiBeforeDowntime)
		{
			var allowSpend = ShouldDumpKenkiBeforeDowntime || !ShouldBankKenkiForBurst || Kenki >= 85 || PartyBurstActive;
			if (SAMSyncPlusPolicy.ShouldSpendKenki(allowSpend, PartyBurstActive || ShouldDumpKenkiBeforeDowntime, Kenki,
				IkishotenPvE.Cooldown.HasOneCharge && ShouldCommitPersonalBurst, IkishotenPvE.EnoughLevel))
			{
				if (HissatsuKyutenPvE.CanUse(out act)) return true;
				if (HissatsuShintenPvE.CanUse(out act)) return true;
			}
		}

		if (ShouldUseBurstMedicine && UseBurstMedicine(out act))
		{
			return true;
		}

		act = null;
		return false;
	}

	private bool ShouldSpendPayload(float readyRemaining, bool spendsKenki = true)
		=> SAMSyncPlusPolicy.ShouldSpendPayload(ShouldCommitPersonalBurst,
			readyRemaining, GCDTime(2), spendsKenki ? Kenki : 0, TargetDying || TargetEndsBeforeBurstPayload);

	protected override bool AllowOgiNamikiri => AllowIaijutsu
		&& (SAMSyncCycle.OgiUrgent(CycleState)
			|| ShouldSpendPayload(StatusHelper.PlayerStatusTime(true, StatusID.OgiNamikiriReady), spendsKenki: false));
	protected override bool AllowIaijutsu => !TimelineAvailable || !DowntimeWithin(IaijutsuCastTimeWindow);
	protected override bool AllowHiganbana => AllowIaijutsu && !TargetDying && !TargetEndsBeforeBurstPayload;

	protected override bool GeneralGCD(out IAction? act)
	{
		_ = Decision;
		if (!TendoSetsugekkaPvE.EnoughLevel || NumberOfHostilesInRange >= 3)
			return base.GeneralGCD(out act);

		var s = CycleState;
		var allowDotTarget = !HiganbanaTargets || NumberOfAllHostilesInRange < 2;
		var selected = SAMSyncCycle.Select(s,
			AllowHiganbana && DamageBuffCovers(HiganbanaPvE) && allowDotTarget && HiganbanaPvE.CanUse(out _, skipComboCheck: true,
				skipStatusProvideCheck: true, skipTTKCheck: IsCurrentTargetBoss || IsInHighEndDuty),
			AllowOgiNamikiri && DamageBuffCovers(OgiNamikiriPvE) && CanUseReadyAction(OgiNamikiriPvE, out _),
			KaeshiNamikiriPvE.CanUse(out _),
			AllowIaijutsu && DamageBuffCovers(TendoSetsugekkaPvE) && CanUseReadyAction(TendoSetsugekkaPvE, out _, skipComboCheck: true),
			AllowIaijutsu && DamageBuffCovers(MidareSetsugekkaPvE) && MidareSetsugekkaPvE.CanUse(out _, skipComboCheck: true),
			CanUseReadyAction(KaeshiSetsugekkaPvE, out _), CanUseReadyAction(TendoKaeshiSetsugekkaPvE, out _));
		IBaseAction? chosen = selected switch
		{
			SAMCycleGcd.Higanbana => HiganbanaPvE,
			SAMCycleGcd.Ogi => OgiNamikiriPvE,
			SAMCycleGcd.KaeshiNamikiri => KaeshiNamikiriPvE,
			SAMCycleGcd.Tendo => TendoSetsugekkaPvE,
			SAMCycleGcd.Midare => MidareSetsugekkaPvE,
			SAMCycleGcd.Kaeshi => KaeshiSetsugekkaPvE,
			SAMCycleGcd.TendoKaeshi => TendoKaeshiSetsugekkaPvE,
			_ => null,
		};
		if (chosen != null && selected is not SAMCycleGcd.Higanbana
			&& CanUseReadyAction(chosen, out act, skipComboCheck: true)) return true;
		if (selected == SAMCycleGcd.Higanbana && chosen != null && chosen.CanUse(out act, skipComboCheck: true,
			skipStatusProvideCheck: selected == SAMCycleGcd.Higanbana,
			skipTTKCheck: selected == SAMCycleGcd.Higanbana && (IsCurrentTargetBoss || IsInHighEndDuty))) return true;
		return CycleCombo(out act);
	}

	private static bool DamageBuffCovers(IBaseAction action)
		=> SAMSyncCycle.RequiredStatusCovers(StatusHelper.PlayerStatusTime(true, StatusID.Fugetsu),
			DataCenter.DefaultGCDRemain, action.Info.CastTime);

	private SAMCycleState CycleState => new(SenCount, HasMeikyoShisui, HasTendo,
		HasFugetsuAndFuka, SchedulerTarget?.StatusTime(true, StatusID.Higanbana) ?? 0f,
		GCDTime(1), StatusHelper.PlayerStatusTime(true, StatusID.Tsubamegaeshi),
		PartyBurstActive, ShouldHoldFlexibleResources,
		MeikyoShisuiPvE.Cooldown.WillHaveXCharges(MeikyoShisuiPvE.Cooldown.MaxCharges, GCDTime(2)),
		DataCenter.ComboTime > 0 && DataCenter.LastComboAction is ActionID.HakazePvE or ActionID.GyofuPvE or ActionID.JinpuPvE or ActionID.ShifuPvE,
		EncounterTime < 30 && !TendoKaeshiSetsugekkaReady,
		TargetDying || TargetEndsBeforeBurstPayload,
		StatusHelper.PlayerStatusTime(true, StatusID.OgiNamikiriReady));

	private bool CycleCombo(out IAction? act)
	{
		// Meikyo finishers never overwrite Sen; restore damage buff before haste.
		if (HasMeikyoShisui)
		{
			// After a long disengage, recover buffs without overwriting a retained Sen.
			if ((!HasFugetsu || WillFugetsuEnd) && HasGetsu && JinpuPvE.CanUse(out act, skipComboCheck: true, skipStatusProvideCheck: true)) return true;
			if ((!HasFuka || WillFukaEnd) && HasKa && ShifuPvE.CanUse(out act, skipComboCheck: true, skipStatusProvideCheck: true)) return true;
			if ((!HasFugetsu || WillFugetsuEnd) && GekkoPvE.CanUse(out act, skipComboCheck: true)) return true;
			if ((!HasFuka || WillFukaEnd) && KashaPvE.CanUse(out act, skipComboCheck: true)) return true;
			if (!HasGetsu && (FugetsuTime <= FukaTime || HasKa) && GekkoPvE.CanUse(out act, skipComboCheck: true)) return true;
			if (!HasKa && KashaPvE.CanUse(out act, skipComboCheck: true)) return true;
			if (!HasGetsu && GekkoPvE.CanUse(out act, skipComboCheck: true)) return true;
			if (!HasSetsu && YukikazePvE.CanUse(out act, skipComboCheck: true)) return true;
		}
		else
		{
			if (!HasGetsu && GekkoPvE.CanUse(out act)) return true;
			if (!HasKa && KashaPvE.CanUse(out act)) return true;
			if ((!HasFuka || WillFukaEnd) && ShifuPvE.CanUse(out act, skipStatusProvideCheck: true)) return true;
			if ((!HasFugetsu || WillFugetsuEnd) && JinpuPvE.CanUse(out act, skipStatusProvideCheck: true)) return true;
			if (!HasSetsu && HasFugetsuAndFuka && YukikazePvE.CanUse(out act)) return true;
			if (!HasGetsu && (FugetsuTime <= FukaTime || HasKa) && JinpuPvE.CanUse(out act, skipStatusProvideCheck: true)) return true;
			if (!HasKa && ShifuPvE.CanUse(out act, skipStatusProvideCheck: true)) return true;
			if (!HasGetsu && JinpuPvE.CanUse(out act, skipStatusProvideCheck: true)) return true;
			if ((SenCount < 3 || !HasFugetsuAndFuka || WillFugetsuEnd || WillFukaEnd) && HakazePvE.CanUse(out act)) return true;
		}
		return EnpiPvE.CanUse(out act);
	}

	private static bool IsTrackedBurstAction(ActionID id) => id is ActionID.IkishotenPvE
		or ActionID.ZanshinPvE or ActionID.OgiNamikiriPvE or ActionID.KaeshiNamikiriPvE
		or ActionID.HissatsuSeneiPvE or ActionID.HissatsuGurenPvE or ActionID.MeikyoShisuiPvE
		or ActionID.TendoSetsugekkaPvE or ActionID.TendoKaeshiSetsugekkaPvE;
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
		if (SAMSyncPlusPolicy.ShouldResetEncounter(_lastEncounterTime, encounterTime, InCombat))
		{
			ReportFight();
			ResetPartyBurstAnchor();
			_lastEncounterTime = -1f;
			_partyBurstStartedAt = -1f;
			_anchorWorthyStartedAt = -1f;
			_ikishotenReadyAt = -1f;
			_lastRotationState = string.Empty;
			_partyBurstWasActive = false;
			_anchorWasWorthy = false;
			_ikishotenWasReady = false;
			_pendingBurstReason = string.Empty;
			_pendingBurstActiveBuffs = 0;
			_pendingBurstExpectedBuffs = 0;
			_lastReportedReasonKey = string.Empty;
			_observedPartyBuffs.Clear();
		}

		_lastEncounterTime = InCombat ? encounterTime : 0f;
		UpdateExpectedPartyBuffs();
		if (!InCombat) return;

		if (_ikishotenWasReady && _decision.Burst == SAMSyncPlusBurstGate.Hold)
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
			&& SAMSyncPlusPolicy.ShouldObservePartyBurst(
				_lastObservedPartyBurstAt, _anchorWorthyStartedAt, PartyBurstCycle))
		{
			_observedPartyBurstCycle = SAMSyncPlusPolicy.AdvancePartyBurstCycle(
				_observedPartyBurstCycle,
				_lastObservedPartyBurstAt,
				_anchorWorthyStartedAt,
				PartyBurstCycle);
			_lastObservedPartyBurstAt = _anchorWorthyStartedAt;
		}

		var ikishotenReady = IkishotenPvE.Cooldown.HasOneCharge;
		if (ikishotenReady && !_ikishotenWasReady)
		{
			_ikishotenReadyAt = encounterTime;
		}
		else if (!ikishotenReady)
		{
			_ikishotenReadyAt = -1f;
		}
		_ikishotenWasReady = ikishotenReady;
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
			Svc.Log.Info($"[SAM Sync] {EncounterTime:F1}s CONFIRMED {cast.Action.Name}; observed buffs {_activePartyBuffs}/{_expectedPartyBuffsCount}; {_decision.Reason} (buff sample at observation, not damage snapshot)");
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

		Svc.Log.Info($"[SAM Sync] Fight report: {_burstUses.Count} burst uses, {_totalHeldSeconds:F1}s scheduler Hold time.");
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
		var anchorJob = AnchorJob == SAMSyncPlusAnchorJob.Automatic ? null : AnchorJob.ToString();
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
			var shouldExpect = SAMSyncPlusPolicy.ShouldExpectPartyBuff(
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
		Svc.Log.Info($"[SAM Sync] Watching party buffs: {(expectedNames.Length == 0 ? "none; personal burst" : expectedNames)}");
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
		=> AnchorJob != SAMSyncPlusAnchorJob.Automatic
			? _anchorJobActive
			: SAMSyncPlusPolicy.IsSignificantPartyBurst(_activeAnchorJobs, _expectedAnchorJobs);

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
				: "first trash burst allowed; Ikishoten reserved for boss");
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
		Svc.Log.Info($"[SAM Sync] Dungeon state: {state}");
	}

	private string _lastRotationState = string.Empty;
	private void LogRotationState()
	{
		if (!LogBurstReport) return;

		var state =
			$"burst:[iki:{(ShouldUseIkishoten ? 1 : 0)} commit:{(ShouldCommitPersonalBurst ? 1 : 0)}] "
			+ $"res[bank:{(ShouldHoldFlexibleResources ? 1 : 0)} meikyo:{(ShouldHoldMeikyoForBurst ? 1 : 0)} bankkenki:{(ShouldBankKenkiForBurst ? 1 : 0)}] "
			+ $"gauge[kenki:{Kenki} sen:{SenCount} ogi:{(HasOgiNamikiri ? 1 : 0)} zanshin:{(HasZanshinReady ? 1 : 0)}] "
			+ $"plan[{_decision.Burst}/{_decision.Resources}: {SAMSyncPlusPolicy.StableDecisionReason(_decision.Reason)}] "
			+ $"buffs[{_activePartyBuffs}/{_expectedPartyBuffsCount}]";

		if (state == _lastRotationState) return;
		_lastRotationState = state;
		Svc.Log.Info($"[SAM Sync] {EncounterTime:F1}s {state}");
	}
	#endregion

	public override void DisplayRotationStatus()
	{
		base.DisplayRotationStatus();
		var decision = Decision;

		ImGui.Text($"SAM Sync: {decision.Burst} - {decision.Reason}");
		ImGui.Text("Burst planning: Tendo, Higanbana, Meikyo, Ogi, Zanshin, Senei/Guren and Kenki.");
		ImGui.Text($"Resources: {(decision.Resources == SAMSyncPlusResourcePlan.Bank ? "banking" : "spending")}");
		var plan = SyncPlusBurstPlan switch
		{
			SAMSyncPlusPlan.Automatic => "Automatic (timeline + dungeons)",
			SAMSyncPlusPlan.TrustParty => "Trust party buffs",
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

		if (IkishotenPvE.Cooldown.HasOneCharge && IkishotenHeldFor > 0f)
		{
			ImGui.Text($"Ikishoten held for: {IkishotenHeldFor:F1}s of {MaximumBurstHoldSeconds:F0}s");
		}
	}

	private static string Describe(float seconds)
		=> seconds is <= 0f or float.MaxValue ? "unknown" : $"{seconds:F1}s";
}

#region Types
internal enum SAMSyncPlusPlan
{
	[Description("Automatic (timeline + dungeons)")]
	Automatic,

	[Description("Trust party buffs")]
	TrustParty,

	[Description("Solo / ignore party buffs")]
	Personal,
}

internal enum SAMSyncPlusAnchorJob
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

internal enum SAMSyncPlusBurstGate
{
	Free,
	Hold,
	Commit,
}

internal enum SAMSyncPlusResourcePlan
{
	Spend,
	Bank,
}

internal enum SAMSyncPlusDowntimeAction
{
	None,
	Dump,
	HoldCeiling,
}

internal readonly record struct SAMSyncPlusDecision(
	SAMSyncPlusBurstGate Burst,
	SAMSyncPlusResourcePlan Resources,
	string Reason,
	SAMSyncPlusDowntimeAction Downtime = SAMSyncPlusDowntimeAction.None)
{
	internal static SAMSyncPlusDecision Default
		=> new(SAMSyncPlusBurstGate.Free, SAMSyncPlusResourcePlan.Spend, "starting up");
}

internal readonly record struct SAMSyncPlusBurstUse(
	float At,
	int ActiveBuffs,
	int ExpectedBuffs,
	string Reason,
	bool InVulnerabilityWindow,
	float DowntimeIn);

internal readonly record struct SAMSyncPlusSnapshot(
	bool AutoBurst,
	bool Reserved,
	bool SynchronizeParty,
	bool RequireLiveParty,
	bool IkishotenReady,
	bool IkishotenReadySoon,
	float IkishotenHeldFor,
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

internal static class SAMSyncPlusScheduler
{
	internal const string DowntimeDumpReason = "downtime too close for a full window; spending resources";
	internal const string DowntimeHoldCeilingReason = "downtime hold ceiling reached; releasing";

	private static bool Known(float seconds) => seconds is > 0f and < float.MaxValue;

	internal static SAMSyncPlusDecision Evaluate(SAMSyncPlusSnapshot s)
	{
		if (!s.AutoBurst)
		{
			return new(SAMSyncPlusBurstGate.Free, SAMSyncPlusResourcePlan.Spend, "burst is off");
		}
		if (s.Reserved)
		{
			return new(SAMSyncPlusBurstGate.Hold, SAMSyncPlusResourcePlan.Bank, "reserved for the next boss");
		}

		var canHold = s.IkishotenHeldFor < s.MaximumHold;
		SAMSyncPlusDecision Hold(string reason, SAMSyncPlusResourcePlan resources)
			=> canHold
				? new(SAMSyncPlusBurstGate.Hold, resources, reason)
				: new(SAMSyncPlusBurstGate.Commit, SAMSyncPlusResourcePlan.Spend, "hold ceiling reached");

		// 1. Downtime feasibility
		if (s.TimelineActive && Known(s.DowntimeIn))
		{
			if (s.DowntimeIn < s.PayloadWindow)
			{
				// The alignment ceiling cannot make an infeasible payload fit before downtime.
				return new(SAMSyncPlusBurstGate.Hold, SAMSyncPlusResourcePlan.Spend, DowntimeDumpReason,
					SAMSyncPlusDowntimeAction.Dump);
			}
			if (s.IkishotenReady && s.DowntimeIn <= s.PayloadWindow + s.LookAhead)
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
					return new(SAMSyncPlusBurstGate.Commit, SAMSyncPlusResourcePlan.Spend,
						"last full window before downtime");
				}
			}
		}

		// 2. Vulnerability windows
		if (s.TimelineActive)
		{
			var insideWindow = Known(s.VulnerableEndIn)
				&& (!Known(s.VulnerableIn) || s.VulnerableEndIn <= s.VulnerableIn);

			if (insideWindow && s.IkishotenReady && s.VulnerableEndIn >= s.PayloadWindow * 0.5f)
			{
				return new(SAMSyncPlusBurstGate.Commit, SAMSyncPlusResourcePlan.Spend,
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
						return Hold($"vulnerability window in {s.VulnerableIn:F1}s", SAMSyncPlusResourcePlan.Bank);
					}
				}
			}
		}

		// 3. Raid buff overlap
		if (s.SynchronizeParty && s.PartyBurstActive)
		{
			if (s.FullPartyBurstActive || s.OverlapWait <= 0f || s.SecondsInPartyBurst >= s.OverlapWait)
			{
				return new(SAMSyncPlusBurstGate.Commit, SAMSyncPlusResourcePlan.Spend, "raid buffs are up");
			}
			return Hold("waiting for the rest of the raid buffs", SAMSyncPlusResourcePlan.Bank);
		}

		if (!s.SynchronizeParty)
		{
			return new(SAMSyncPlusBurstGate.Commit, SAMSyncPlusResourcePlan.Spend, "no party sync; personal burst");
		}

		// 4. Fallback prediction
		if (s.RequireLiveParty && s.IkishotenReadySoon)
		{
			return Hold("waiting for a live raid buff", SAMSyncPlusResourcePlan.Bank);
		}
		if (s.IkishotenReadySoon && s.PredictedPartyBurstIn.HasValue)
		{
			var predicted = s.PredictedPartyBurstIn.Value;
			if (predicted <= s.LookAhead && predicted >= -s.MaximumHold)
			{
				return Hold($"raid buffs predicted in {predicted:F1}s", SAMSyncPlusResourcePlan.Bank);
			}
		}

		return new(SAMSyncPlusBurstGate.Commit, SAMSyncPlusResourcePlan.Spend, "no reason to wait");
	}
}

internal static class SAMSyncPlusPolicy
{
	internal static bool AllowMedicine(bool commit, float encounterTime, bool useSecond, float secondTime)
		=> commit && (encounterTime < 30f || (useSecond && encounterTime >= secondTime));

	internal static bool ShouldUseSenei(bool ikishotenCooling, bool targetEnding, bool downtimeDump,
		bool banking, int kenki, bool zanshinReady)
		=> (ikishotenCooling || targetEnding || downtimeDump) && kenki >= 25
			&& (!banking || targetEnding || downtimeDump) && (!zanshinReady || kenki >= 75);
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

	internal static bool ShouldUseIkishoten(
		bool commitBurst,
		bool kenkiAllows,
		bool targetEndsBeforePayload,
		bool targetAlive)
		=> commitBurst && kenkiAllows && !targetEndsBeforePayload && targetAlive;

	internal static bool ShouldHoldMeikyo(
		bool synchronizeParty,
		bool partyBurstActive,
		ushort currentCharges,
		ushort maxCharges,
		bool willCapSoon,
		float? predictedPartyBurstIn,
		float lookAhead)
	{
		// If Meikyo is at max charges or about to cap, never hold (spend to avoid overcap)
		if (currentCharges >= maxCharges || willCapSoon) return false;

		// If party buffs are active now, never hold (use for burst)
		if (partyBurstActive) return false;

		// If burst is predicted soon and we only have 1 charge banked, hold it for burst
		if (synchronizeParty && predictedPartyBurstIn is > 0f and float predicted && predicted <= lookAhead)
		{
			return true;
		}

		return false;
	}

	internal static bool ShouldSpendPayload(bool commit, float readyRemaining, float safetyWindow,
		int kenki, bool targetEnding)
		=> commit || readyRemaining <= safetyWindow || kenki >= 85 || targetEnding;

	internal static bool ShouldSpendKenki(bool allowSpend, bool liveBuffs, int kenki,
		bool ikishotenReady, bool hasIkishoten)
		=> (allowSpend || (ikishotenReady && kenki > 50))
			&& kenki >= (liveBuffs || !hasIkishoten ? 25 : 50);

	internal static bool ShouldBankKenki(
		bool holdResources,
		int currentKenki,
		int bankThreshold)
		=> holdResources && currentKenki < bankThreshold;

	internal static bool ShouldDumpKenkiBeforeDowntime(
		bool timelineActive,
		float downtimeIn,
		int currentKenki)
		=> timelineActive && downtimeIn is > 0f and <= 3.0f && currentKenki >= 25;

	internal static float EffectiveOverlapWait(
		float shortestActiveBuffRemaining,
		float payloadWindow,
		float overlapWait)
		=> shortestActiveBuffRemaining <= payloadWindow + 1f ? 0f : overlapWait;

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

	internal static bool ShouldUseSecondMedicine(
		bool enabled,
		float configuredTime,
		float encounterTime,
		SAMSyncPlusBurstGate burstGate)
		=> enabled && burstGate == SAMSyncPlusBurstGate.Commit && encounterTime >= configuredTime;
}
