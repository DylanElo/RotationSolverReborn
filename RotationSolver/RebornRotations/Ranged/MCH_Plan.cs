using RotationSolver.IPC;

namespace RotationSolver.RebornRotations.Ranged;

/// <summary>
/// MCH Sync with the burst timing taken from an authored plan instead of inferred from raid buffs.
/// Windows are entries on the "Burst" track of BossMod Reborn's "Utility: MCH" planner module, anchored
/// to boss states like any mitigation entry. BossMod reports them with no action, so nothing auto-casts
/// them; this rotation reads them and decides Wildfire, Barrel and potions from them. Execution, weave
/// ordering and the target/downtime/phase-objective guards are inherited unchanged. Without a plan for
/// the fight it behaves exactly like MCH Sync.
/// </summary>
[Rotation("MCH Plan", CombatType.PvE, GameVersion = "7.55",
	Description = "MCH Sync with burst windows read from your BossMod Reborn plan (Utility: MCH > Burst track). Without a plan it plays exactly like MCH Sync.")]
public sealed class MCH_Plan : MCH_SyncPlus
{
	/// <summary>Must see past one Wildfire recast to know whether a use now is back for the next window.</summary>
	private const float PlanLookAhead = 150f;
	private const float PollInterval = 1f;

	/// <summary>Buffs that land after a potion window has passed still get its potion, for this long.</summary>
	private const float LatePotionGrace = 30f;

	private List<BMRPlannedAction> _windows = [];
	private DateTime _potionWindowEnd = DateTime.MinValue;
	private DateTime _windowEnd = DateTime.MinValue;
	private DateTime _polledAt = DateTime.MinValue;
	private float _lastEncounterTime;
	private int _planVersion;

	/// <summary>
	/// A Burst window was seen this pull, so the plan is active. BossMod cannot see past a kill-gated phase end: from the last P4
	/// window until P5 starts it reports nothing (replayed), and reading that as "no plan" handed Wildfire and the timed potions
	/// back to MCH Sync's fallbacks in the P4->P5 gap.
	/// </summary>
	private bool _planSeen;

	/// <summary>Stands in for the next window while the plan is active but BossMod shows none (treated like a far-off window).</summary>
	private static readonly MCHPlanWindow NextPhaseWindow = new(999f, 1019f, false);

	/// <summary>Planned Burst entries, refreshed once a second, with times corrected for the age of the poll.</summary>
	private IEnumerable<MCHPlanWindow> Windows
	{
		get
		{
			var now = DateTime.Now;
			// A wipe (out of combat, or the combat timer restarting) ends the old pull's plan state: otherwise a quick
			// re-pull could inherit last pull's potion window, cached windows, or the plan latch (no-plan fallback lost).
			// BossMod unloading or swapping the plan mid-pull drops the latch too: it only bridges a hidden window of the same plan.
			var encounterTime = EncounterTime;
			var planVersion = BMRPlan_IPCSubscriber.PlanVersion;
			if (!InCombat || encounterTime < _lastEncounterTime || !BMRPlan_IPCSubscriber.IsEnabled || planVersion != _planVersion)
			{
				_planVersion = planVersion;
				_windowEnd = _potionWindowEnd = _polledAt = DateTime.MinValue;
				_windows = [];
				_planSeen = false;
			}
			_lastEncounterTime = encounterTime;
			if (now - _polledAt >= TimeSpan.FromSeconds(PollInterval))
			{
				_polledAt = now;
				_windows = InCombat && BMRPlan_IPCSubscriber.IsEnabled
					? BMRPlan_IPCSubscriber.GetUpcomingPlannedActions(PlanLookAhead)
						.Where(a => a.TrackName == "Burst" && a.ModuleType.EndsWith("ClassMCHUtility", StringComparison.Ordinal))
						.ToList()
					: [];
				_planSeen = InCombat && (_planSeen || _windows.Count > 0);
			}
			var age = (float)(now - _polledAt).TotalSeconds;
			return _windows.Select(a => new MCHPlanWindow(a.ActivationIn - age, a.WindowEndIn - age, a.OptionName == "WindowWithPotion"))
				.Where(w => w.EndIn > 0f);
		}
	}

	private MCHPlanWindow? CurrentWindow => Windows.OrderBy(w => w.StartIn).Cast<MCHPlanWindow?>().FirstOrDefault()
		?? (_planSeen ? NextPhaseWindow : null);

	private protected override MCHSyncPlusDecision? PlannedDecision()
	{
		if (CurrentWindow is not { } window) return null;
		if (window.StartIn <= 0f) _windowEnd = DateTime.Now.AddSeconds(window.EndIn);
		return MCHPlanScheduler.Evaluate(new MCHPlanSnapshot(
			IsBurst,
			ReserveBurst,
			ObjectiveBanked,
			TimelineAvailable ? DowntimeIn : float.MaxValue,
			WildfirePayloadWindow,
			window.StartIn,
			PartyBurstActive,
			FullPartyBurstActive,
			SecondsInPartyBurst,
			OverlapWait,
			DateTime.Now < _windowEnd.AddSeconds(LatePotionGrace)));
	}

	/// <summary>
	/// Potion only in a planned potion window, right as Wildfire is about to go. BossMod drops a window once it ends,
	/// so its potion is remembered for a grace period: late buffs that fire Wildfire still get the potion.
	/// </summary>
	private protected override bool? PlannedPotionNow
	{
		get
		{
			var window = CurrentWindow;
			// Armed only once the window has opened, like PlannedDecision: CurrentWindow can be up to 150s ahead,
			// and arming on a future window made any earlier raid buff read as its late grace.
			if (window is { Potion: true, StartIn: <= 0f } open) _potionWindowEnd = DateTime.Now.AddSeconds(open.EndIn);
			// ReserveBurst covers Auto Burst off and the dungeon reserve; a banked phase end pots in the next phase.
			if (window is { } next && !ReserveBurst && !ObjectiveBanked && MCHPlanScheduler.PotsAhead(next.Potion, next.StartIn, WildfirePvE.Cooldown.WillHaveOneCharge(next.StartIn)))
				return true;
			return MCHPlanScheduler.PotionDue(window, PartyBurstActive, DateTime.Now < _potionWindowEnd.AddSeconds(LatePotionGrace)) is bool due
				? due && WildfirePvE.Cooldown.HasOneCharge && ShouldUseWildfire
				: null;
		}
	}

	/// <summary>Reassemble and the fallback paths align to the planned window instead of a learned cadence.</summary>
	// The NextPhaseWindow stand-in (999s) reaches the downtime Barrel too, which reads it as "next burst far enough".
	// In FRU that is what lets the CT Barrel fire (P5's window is hidden behind the P4 kill but really ~117s+ out).
	// Do not turn unknown into null here: that kills the CT Barrel. It only misfires where a hidden window comes <110s
	// after a downtime; fix that with a real lower bound for the hidden window, not by dropping the stand-in.
	private protected override float? SecondsUntilExpectedPartyBurst
		=> CurrentWindow is { StartIn: > 0f } window ? window.StartIn : base.SecondsUntilExpectedPartyBurst;

	private protected override float? BarrelPreparationIn
		=> CurrentWindow is { } window ? Math.Max(0f, window.StartIn) : base.BarrelPreparationIn;
}

internal readonly record struct MCHPlanWindow(float StartIn, float EndIn, bool Potion);

internal readonly record struct MCHPlanSnapshot(
	bool AutoBurst,
	bool Reserved,
	bool ObjectiveBanked,
	float DowntimeIn,
	float PayloadWindow,
	float WindowStartIn,
	bool LiveRaidBuffs,
	bool AllRaidBuffs = false,
	float SecondsInBurst = 0f,
	float OverlapWait = 0f,
	bool JustAfterWindow = false);

/// <summary>The plan-driven burst decision, as one pure function over a snapshot.</summary>
internal static class MCHPlanScheduler
{
	/// <summary>Live raid buffs this close to a planned start pull the window in to them.</summary>
	internal const float WindowSlide = 8f;

	/// <summary>
	/// A potion window pots up to this far ahead (Wildfire must be back for it): the 30s tincture still covers the
	/// burst and is back that much sooner. FRU: the crystals pot has to be back for the P4 burst when Oracle appears.
	/// </summary>
	internal const float PotionLead = 10f;

	internal static bool PotsAhead(bool potion, float startIn, bool wildfireBackInTime)
		=> potion && startIn > 0f && startIn <= PotionLead && wildfireBackInTime;

	/// <summary>The window is where the burst is expected; live raid buffs are what confirm it.</summary>
	internal static bool IsOpen(float startIn, float endIn, bool liveRaidBuffs)
		=> liveRaidBuffs && endIn > 0f && startIn <= WindowSlide;

	/// <summary>
	/// Null hands potions to the timed fallbacks. A remembered window's grace outlives the window itself:
	/// BossMod drops the last one when it ends, and late buffs must still get its potion.
	/// </summary>
	internal static bool? PotionDue(MCHPlanWindow? window, bool liveRaidBuffs, bool inLateGrace)
		=> window is { Potion: true } w && IsOpen(w.StartIn, w.EndIn, liveRaidBuffs) || liveRaidBuffs && inLateGrace ? true
			: window is null ? null : false;

	internal static MCHSyncPlusDecision Evaluate(MCHPlanSnapshot s)
	{
		if (!s.AutoBurst) return new(MCHSyncPlusBurstGate.Free, MCHSyncPlusResourcePlan.Spend, "burst is off");
		if (s.Reserved) return new(MCHSyncPlusBurstGate.Hold, MCHSyncPlusResourcePlan.Bank, "reserved for the next boss");
		if (s.ObjectiveBanked)
			return new(MCHSyncPlusBurstGate.Hold, MCHSyncPlusResourcePlan.Bank, MCHSyncPlusScheduler.PhaseObjectiveReason);
		if (s.DowntimeIn is >= 0f && s.DowntimeIn < s.PayloadWindow)
			return new(MCHSyncPlusBurstGate.Hold, MCHSyncPlusResourcePlan.Spend, MCHSyncPlusScheduler.DowntimeDumpReason,
				MCHSyncPlusDowntimeAction.Dump);

		// Late buffs still count after the planned window has passed; Wildfire and Barrel never go without them.
		// Partial buffs wait for the rest like MCH Sync (Wildfire snapshots the buffs live when it is cast),
		// unless downtime is too close to wait.
		// Partial buffs ahead of the window (an early Divination in P4: Wildfire on 2/4 with the party's
		// burst at 10:42) bank until the window opens, and the overlap wait counts from the opening, not the first buff.
		if (s.LiveRaidBuffs)
		{
			var ahead = s.WindowStartIn > 0f && !s.JustAfterWindow;
			var secondsIn = ahead ? Math.Min(s.SecondsInBurst, WindowSlide - s.WindowStartIn) : s.SecondsInBurst;
			var remainingWait = Math.Max(0f, s.OverlapWait - secondsIn);
			var noTimeToWait = s.DowntimeIn is >= 0f and < float.MaxValue && remainingWait + s.PayloadWindow > s.DowntimeIn;
			if (ahead && s.WindowStartIn > WindowSlide && !s.AllRaidBuffs && !noTimeToWait)
				return new(MCHSyncPlusBurstGate.Hold, MCHSyncPlusResourcePlan.Bank, $"raid buffs early; planned window in {s.WindowStartIn:F1}s");
			return s.AllRaidBuffs || remainingWait <= 0f || noTimeToWait
				? new(MCHSyncPlusBurstGate.Commit, MCHSyncPlusResourcePlan.Spend, "raid buffs are up")
				: new(MCHSyncPlusBurstGate.Hold, MCHSyncPlusResourcePlan.Bank, "waiting for the rest of the raid buffs");
		}
		if (s.WindowStartIn <= WindowSlide)
			return new(MCHSyncPlusBurstGate.Hold, MCHSyncPlusResourcePlan.Bank,
				s.WindowStartIn > 0f ? $"planned window in {s.WindowStartIn:F1}s" : "planned window open; waiting for raid buffs");
		return new(MCHSyncPlusBurstGate.Hold, MCHSyncPlusResourcePlan.Spend, $"saving Wildfire for the planned window in {s.WindowStartIn:F0}s");
	}
}
