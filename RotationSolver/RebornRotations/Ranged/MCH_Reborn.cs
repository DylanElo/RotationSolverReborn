namespace RotationSolver.RebornRotations.Ranged;

[Rotation("Reborn", CombatType.PvE, GameVersion = "7.55")]
[SourceCode(Path = "main/RebornRotations/Ranged/MCH_Reborn.cs")]

public class MCH_Reborn : MachinistRotation
{
	protected const float WildfireLateWeaveWindow = 1f;

	/// <summary>
	/// Seconds of uptime a Wildfire window needs to land its six weaponskills. Lives here, not on
	/// the derived scheduler, because the base rotation's own downtime block needs the same number
	/// and was carrying it as a bare 10f literal - two definitions of one window, either of which
	/// could be tuned without the other.
	/// </summary>
	protected const float WildfirePayloadWindow = 10f;

	#region Config Options
	[RotationConfig(CombatType.PvE, Name = "Countdown potion",
		Section = "Potions", Order = 200,
		Tooltip = "Uses the configured burst medicine during the countdown when Auto Burst is enabled. MCH Sync also allows it on the opening Wildfire when you pull without a countdown.")]
	protected bool OpenerBurstMeds { get; set; } = false;

	[RotationConfig(CombatType.PvE, Name = "Show utility and safety settings",
		Section = "Utility and safety", Order = 300,
		Tooltip = "Reveals optional movement, target, mitigation, countdown, and downtime controls.")]
	protected bool ShowMchUtilitySettings { get; set; } = false;

	[RotationConfig(CombatType.PvE, Name = "Bioblaster while moving",
		Parent = nameof(ShowMchUtilitySettings),
		Section = "Utility and safety", Order = 310,
		Tooltip = "Allows Bioblaster as a movement fallback when its normal target checks pass.")]
	protected bool BioMove { get; set; } = true;

	[RotationConfig(CombatType.PvE, Name = "Boss-only Wildfire",
		Parent = nameof(ShowMchUtilitySettings),
		Section = "Utility and safety", Order = 320,
		Tooltip = "Restricts Wildfire to targets identified as bosses.")]
	protected bool WildfireBoss { get; set; } = false;

	[RotationConfig(CombatType.PvE, Name = "Avoid overlapping mitigation",
		Parent = nameof(ShowMchUtilitySettings),
		Section = "Utility and safety", Order = 330,
		Tooltip = "Prevents Tactician and Dismantle from overlapping equivalent active mitigation.")]
	protected bool MitOverlap { get; set; } = true;

	[RotationConfig(CombatType.PvE, Name = "Air Anchor at 1s in countdown",
		Parent = nameof(ShowMchUtilitySettings),
		Section = "Utility and safety", Order = 340,
		Tooltip = "Queues Air Anchor when one second remains in the pull countdown.")]
	protected bool AirAnchorCountdown { get; set; } = false;

	[RotationConfig(CombatType.PvE, Name = "Tactician only for multiple targets",
		Parent = nameof(ShowMchUtilitySettings),
		Section = "Utility and safety", Order = 350,
		Tooltip = "Requires more than one hostile target before automatic Tactician use.")]
	protected bool MultiTact { get; set; } = false;

	[RotationConfig(CombatType.PvE, Name = "Spend Heat before predicted downtime",
		Parent = nameof(ShowMchUtilitySettings),
		Section = "Utility and safety", Order = 360,
		Tooltip = "Uses BossMod Reborn downtime prediction to spend otherwise stranded Heat while preserving an earlier Wildfire window.")]
	public bool BmrDumpBeforeDowntime { get; set; } = true;
	#endregion

	#region Countdown logic
	protected override IAction? CountDownAction(float remainTime)
	{
		if (AirAnchorCountdown && remainTime < 1f && AirAnchorPvE.EnoughLevel && AirAnchorPvE.CanUse(out var act))
		{
			return act;
		}

		if (!AirAnchorCountdown && remainTime < 0.1f && AirAnchorPvE.EnoughLevel && AirAnchorPvE.CanUse(out act))
		{
			return act;
		}

		if (remainTime < 4.75f && ReassemblePvE.CanUse(out act))
		{
			return act;
		}

		if (AirAnchorCountdown && IsBurst && OpenerBurstMeds && remainTime <= 1.5f && UseBurstMedicine(out act))
		{
			return act;
		}

		if (!AirAnchorCountdown && IsBurst && OpenerBurstMeds && remainTime <= 1f && UseBurstMedicine(out act))
		{
			return act;
		}

		return base.CountDownAction(remainTime);
	}
	#endregion

	/// <summary>
	/// True when Wildfire is coming soon (within 15s) and we should save Heat.
	/// </summary>
	private bool IsPreBurst => WildfirePvE.EnoughLevel
		&& WildfirePvE.Cooldown.IsCoolingDown
		&& !WildfirePvE.Cooldown.HasOneCharge
		&& WildfirePvE.Cooldown.RecastTimeRemain <= 15;

	private bool ShouldRecoverLostFullMetalBurst => ShouldCommitPersonalBurst
		&& FullMetalFieldPvE.EnoughLevel
		&& WildfirePvE.Cooldown.HasOneCharge
		&& !HasFullMetalMachinist
		&& BarrelStabilizerPvE.Cooldown.ElapsedAfter(5f);

	/// <summary>
	/// Allows derived rotations to delay the flexible parts of the burst without
	/// changing Reborn's action priorities or safety checks.
	/// </summary>
	protected virtual bool ShouldCommitPersonalBurst => IsBurst;

	/// <summary>
	/// Whether Wildfire should be committed on the current target.
	/// </summary>
	protected virtual bool ShouldUseWildfire => ShouldCommitPersonalBurst;

	/// <summary>
	/// Holds resources that can safely wait while preserving overcap protection.
	/// </summary>
	protected virtual bool ShouldHoldFlexibleResources => false;

	/// <summary>
	/// Releases a remaining Reassemble charge onto Drill when waiting would lose the use.
	/// </summary>
	protected virtual bool ShouldSpendRemainingReassemble => false;

	/// <summary>
	/// Withholds a ready Reassemble from the next eligible tool GCD so it can land inside an
	/// incoming raid-buff window instead. The caller is responsible for never returning true at
	/// max charges - holding a capped charge is a pure loss, not a delay.
	/// </summary>
	protected virtual bool ShouldHoldReassembleForBuffs => false;

	/// <summary>
	/// Fires when a Queen deployment is SELECTED, with the Battery it would go out at - not when
	/// the cast lands. RotationSolver re-evaluates the same frame speculatively for its next-action
	/// preview, so one real deployment raises this repeatedly; an implementer that logs or counts
	/// must debounce. A no-op by default.
	/// </summary>
	protected virtual void OnQueenDeployed(int battery) { }

	/// <summary>
	/// Allows a derived rotation to release Queen slightly before other burst resources.
	/// </summary>
	protected virtual bool ShouldHoldQueen => ShouldHoldFlexibleResources;
	/// <summary>
	/// A schedule invariant that outranks the generic deploy rules. Takes the projected Battery
	/// gain so an override can still let a deploy through rather than overcap the gauge.
	/// </summary>
	protected virtual bool ShouldHardHoldQueen(int projectedBatteryGain) => false;

	/// <summary>
	/// Whether Excavator should sit on its 30s window rather than being spent the moment Chain Saw
	/// grants it. Excavator pays a fixed 20 Battery, so spending it blind can overshoot a scheduled
	/// Queen target that a 10-Battery Clean Shot would have hit exactly - the delay The Balance
	/// describes for the odd-minute Queens. Default false keeps the base rotation spending on sight.
	/// </summary>
	protected virtual bool ShouldHoldExcavatorForBattery => false;
	protected virtual bool ShouldReleaseQueenAtMinimumBattery => false;

	/// <summary>
	/// The fight's first Queen, released at the opener's 60 Battery rather than banked. The clock
	/// is 30s, not the 15s this used to carry: the opener reaches 60 on the Excavator that follows
	/// the opener Hypercharge, and a Hypercharge window is a fixed 7.5s, so that Excavator lands at
	/// ~18s every pull (17.7s and 18.0s measured). At 15s this could never fire, which left the
	/// base rotation with no opener release at all and the opener Queen banked to 100.
	/// A derived rotation that tracks per-fight state should override this with a real
	/// "no Queen has deployed yet" test instead of a clock.
	/// </summary>
	protected virtual bool QueenOpenerRelease
		=> Battery == 60 && IsLastGCD(false, ExcavatorPvE) && CombatTime < 30f;

	/// <summary>
	/// The Boss-only Wildfire restriction, checked against the target the action actually resolved
	/// to. Written out at four separate call sites before, each dereferencing Target.Target with no
	/// null guard; a derived rotation's predictive gate reads the player's target instead, because
	/// it has to answer before CanUse resolves anything. Those two answers can differ on an add
	/// pull, so keep them distinguishable: this is the authoritative post-resolution check, and a
	/// gate that disagrees only ever costs a wasted evaluation, never a wrong cast.
	/// </summary>
	protected bool WildfireResolvedTargetAllowed
		=> !WildfireBoss || WildfirePvE.Target.Target?.IsBossFromIcon() == true;

	/// <summary>
	/// Enough Heat to open a Hypercharge window, counting the free use Barrel Stabilizer grants.
	/// Was written out three times; the two halves are not interchangeable (Hypercharged pays no
	/// Heat at all), so a copy that drifted to one half would silently change burst feasibility.
	/// </summary>
	protected bool HasHeatForHypercharge => Heat >= 50 || HasHypercharged;

	/// <summary>
	/// Whether Heat may be committed on the current target.
	/// </summary>
	protected virtual bool ShouldUseHypercharge => true;

	/// <summary>Whether a normal, non-expiring Full Metal Field may be committed.</summary>
	protected virtual bool ShouldUseFullMetalField => true;

	/// <summary>Whether a cooldown should be saved for the next phase instead of spent now.</summary>
	protected virtual bool ShouldHoldTool(IBaseAction tool) => false;

	/// <summary>Holds Barrel Stabilizer when its Hypercharged and Full Metal Machinist would have nothing to hit.</summary>
	protected virtual bool ShouldHoldBarrel => false;

	/// <summary>Barrel Stabilizer may go on cooldown without a committed burst (MCH Sync: FRU P3/P4).</summary>
	protected virtual bool ShouldUseBarrelOnCooldown => false;

	/// <summary>
	/// Whether BossMod Reborn timeline data may influence this rotation at all. Every timeline
	/// read in this class and its derivations routes through <see cref="DowntimeWithin"/> and
	/// <see cref="DowntimeIn"/> so that a derived rotation switching this off actually switches
	/// the timeline off - the downtime blocks below used to keep firing regardless, while the
	/// scheduler that knows how to recover from them went quiet.
	/// </summary>
	protected virtual bool TimelineEnabled => true;

	/// <summary>Downtime proximity, honouring <see cref="TimelineEnabled"/>.</summary>
	// Includes the Ice Veil's cast-bound end: BMR reports it as unknown, and the Queen guard missed it
	// (logs: 50-Battery Queen 4.4s before the veil vanished, overdriven 3s later).
	protected bool DowntimeWithin(float seconds) => (TimelineEnabled && BMRDowntimeWithin(seconds)) || CastBoundTargetLifetime <= seconds;

	/// <summary>Seconds until predicted downtime, or MaxValue when the timeline is off.</summary>
	protected float DowntimeIn => Math.Min(TimelineEnabled ? BMRDowntimeIn : float.MaxValue, CastBoundTargetLifetime);

	/// <summary>
	/// FRU Ice Veil (0x45A5) is destroyed when Endless Ice Age finishes casting, and BMR reports that downtime as unknown
	/// (HP-gated phase). Without this a Wildfire was released 6s before the veil vanished and dealt 0.
	/// </summary>
	private static float CastBoundTargetLifetime
		=> (CurrentTarget ?? HostileTarget) is { BaseId: 0x45A5, IsCasting: true } veil
			? Math.Max(0f, veil.TotalCastTime - veil.CurrentCastTime)
			: float.MaxValue;

	protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
	{
		if (ShouldUseHypercharge && HyperchargePvE.EnoughLevel)
		{
			if (MCHBurstActionPolicy.ShouldForceCappedHypercharge(
				Heat == 100, HasWildfire, HasFullMetalMachinist)
				&& HyperchargePvE.CanUse(out act, skipTTKCheck: true))
			{
				return true;
			}

			if (!WildfirePvE.EnoughLevel)
			{
				if (HyperchargePvE.CanUse(out act, skipTTKCheck: true))
				{
					return true;
				}
			}
			// Heat, not Battery. Hypercharge is paid for out of the Heat gauge; reading the Queen
			// gauge here fired this branch on a full Battery that Hypercharge cannot spend, and
			// stayed silent on the capped Heat it exists to dump. Sub-level-100 path only, which is
			// why it went unnoticed.
			if (!FullMetalFieldPvE.EnoughLevel && (HasWildfire || (WildfirePvE.Cooldown.IsCoolingDown && Heat == 100)))
			{
				if (HyperchargePvE.CanUse(out act, skipTTKCheck: true))
				{
					return true;
				}
			}
			if (MCHBurstActionPolicy.ShouldStartWildfireHypercharge(HasWildfire, HasFullMetalMachinist))
			{
				if (HyperchargePvE.CanUse(out act, skipTTKCheck: true))
				{
					return true;
				}
			}

			// === BMR: Dump Heat before downtime ===
			// If downtime is imminent (<=15s) and we're not in Wildfire, spend Heat aggressively
			// to avoid losing gauge value during the untargetable phase.
			// Skip if we're already in pre-burst window (Wildfire coming soon and downtime is far).
			if (MCHBurstActionPolicy.ShouldDumpHeatBeforeDowntime(
				BmrDumpBeforeDowntime,
				DowntimeWithin(15f),
				HasWildfire,
				IsOverheated,
				HasHeatForHypercharge,
				IsPreBurst,
				WildfirePvE.Cooldown.RecastTimeRemain,
				DowntimeIn,
				ShouldHoldFlexibleResources))
			{
				if (HyperchargePvE.CanUse(out act, skipTTKCheck: true))
				{
					return true;
				}
			}
		}

		return base.EmergencyAbility(nextGCD, out act);
	}

	#region oGCD Logic
	[RotationDesc(ActionID.TacticianPvE)]
	protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
	{
		var dismantleActive = (CurrentTarget ?? HostileTarget)?.HasStatus(false, StatusID.Dismantled) == true;
		if (MCHMitigationPolicy.ShouldUseTactician(MitOverlap, dismantleActive)
			&& (!MultiTact || NumberOfAllHostilesInMaxRange > 1))
		{
			if (TacticianPvE.CanUse(out act))
			{
				return true;
			}
		}

		if (TryUseDismantle(out act))
		{
			return true;
		}

		return base.DefenseAreaAbility(nextGCD, out act);
	}

	[RotationDesc(ActionID.DismantlePvE)]
	protected override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
	{
		if (TryUseDismantle(out act))
		{
			return true;
		}

		return base.DefenseSingleAbility(nextGCD, out act);
	}

	private bool TryUseDismantle(out IAction? act)
	{
		act = null;
		var rangedMitigationActive = StatusHelper.PlayerHasStatus(false, StatusHelper.RangePhysicalDefense);
		return MCHMitigationPolicy.ShouldUseDismantle(MitOverlap, rangedMitigationActive)
			&& DismantlePvE.CanUse(out act);
	}

	/// <summary>
	/// Reassemble crits every enemy a tool hits: with one charge, Chain Saw due the GCD after Air Anchor and 2+ enemies in its
	/// line, the charge goes on Chain Saw. Not at max charges (it would overcap) or on a dump request.
	/// </summary>
	private bool HoldReassembleForChainSawCleave(IBaseAction airAnchor)
	{
		if (!ChainSawPvE.EnoughLevel || ShouldSpendRemainingReassemble
			|| ReassemblePvE.Cooldown.CurrentCharges >= ReassemblePvE.Cooldown.MaxCharges
			|| !ChainSawPvE.Cooldown.WillHaveOneCharge(DataCenter.DefaultGCDRemain + GCDTime(1))
			|| Player == null || airAnchor.Target.Target is not { } aim)
		{
			return false;
		}

		static System.Numerics.Vector2 Xz(IGameObject o) => new(o.Position.X, o.Position.Z);
		return MCHBurstActionPolicy.LineHits(Xz(Player), Xz(aim), AllHostileTargets.Select(t => (Xz(t), t.HitboxRadius))) >= 2;
	}

	// Logic for using attack abilities outside of GCD, focusing on burst windows and cooldown management.
	protected override bool AttackAbility(IAction nextGCD, out IAction? act)
	{
		if (FullMetalFieldPvE.EnoughLevel && HasFullMetalMachinist && IsLastAction(false, WildfirePvE))
		{
			return base.AttackAbility(nextGCD, out act);
		}

		// Reassemble Logic
		// Check next GCD action and conditions for Reassemble.
		var isReassembleUsable =
			nextGCD is IBaseAction reassembleTool && reassembleTool.Cooldown.WillHaveOneCharge(0) &&
			//Reassemble current # of charges and double proc protection
			ReassemblePvE.Cooldown.CurrentCharges > 0 && !HasReassembled &&
			(nextGCD.IsTheSameTo(true, [ChainSawPvE, ExcavatorPvE])
			|| (!ChainSawPvE.EnoughLevel && nextGCD.IsTheSameTo(true, SpreadShotPvE) && ((IBaseAction)nextGCD).Target.AffectedTargets.Length >= (SpreadShotMasteryTrait.EnoughLevel ? 4 : 5))
			|| (nextGCD.IsTheSameTo(false, [AirAnchorPvE]) && !HoldReassembleForChainSawCleave((IBaseAction)nextGCD))
			|| (!ChainSawPvE.EnoughLevel && nextGCD.IsTheSameTo(true, DrillPvE))
			|| (!DrillPvE.EnoughLevel && nextGCD.IsTheSameTo(true, CleanShotPvE))
			|| (!CleanShotPvE.EnoughLevel && nextGCD.IsTheSameTo(false, HotShotPvE))
			|| MCHBurstActionPolicy.ShouldSpendReassembleOnDrill(
				ReassemblePvE.Cooldown.CurrentCharges >= ReassemblePvE.Cooldown.MaxCharges,
				ShouldSpendRemainingReassemble,
				nextGCD.IsTheSameTo(false, DrillPvE)
				// a single Drill charge gets banked once the burst starts; the bank flips between this weave and the GCD,
				// so 26 FRU Reassembles (fixed spots ~2:06 / ~4:07) landed on a filler. Wait for the banked Drill instead.
				&& (DrillPvE.Cooldown.CurrentCharges >= 2
					|| !(WildfirePvE.Cooldown.WillHaveOneCharge(3f) || BarrelStabilizerPvE.Cooldown.WillHaveOneCharge(3f)))));
		// Attempt to use Reassemble if it's ready
		if (isReassembleUsable && (!ShouldHoldFlexibleResources
			|| ReassemblePvE.Cooldown.CurrentCharges >= ReassemblePvE.Cooldown.MaxCharges
			|| ShouldSpendRemainingReassemble)
			// An explicit dump request (dying target, downtime) outranks the alignment hold -
			// there is no later window left to save the charge for.
			&& (ShouldSpendRemainingReassemble || !ShouldHoldReassembleForBuffs)
			&& !ShouldHoldTool(ReassemblePvE))
		{
			if (ReassemblePvE.CanUse(out act, usedUp: true))
			{
				return true;
			}
		}

		// Gated on the committed burst, NOT "on cooldown" as the guides phrase it. Tried ungated once and
		// reverted the same session. The guides say on cooldown because holding a
		// cooldown normally risks losing a use - that cannot happen here: Barrel Stabilizer is a
		// flat 120s with no charges, and so is Wildfire, so a delay bounded by the 30s hold ceiling
		// never costs a usage. What ungating DOES cost is the buff itself. Barrel Stabilizer grants
		// Hypercharged and Full Metal Machinist for exactly 30s, which is also MaximumWildfireHold,
		// so a full-length hold can expire Full Metal Machinist before Wildfire ever commits and
		// throw away Full Metal Field (945 effective potency). Worse, with Auto Burst off
		// ShouldCommitPersonalBurst is false permanently, so an ungated Barrel Stabilizer would
		// burn both buffs every 120s for nothing.
		if (ShouldCommitPersonalBurst || ShouldUseBarrelOnCooldown)
		{
			var bmrBlockBarrel = DowntimeWithin(GCDTime(2));
			if (!bmrBlockBarrel && !ShouldHoldBarrel && BarrelStabilizerPvE.CanUse(out act))
			{
				return true;
			}
		}

		var LowLevelHyperCheck = !AutoCrossbowPvE.EnoughLevel && SpreadShotPvE.CanUse(out _);
		var bmrBlockWildfire = DowntimeWithin(WildfirePayloadWindow);
		if (ShouldUseWildfire && !bmrBlockWildfire)
		{
			if (FullMetalFieldPvE.EnoughLevel)
			{
				if (CanLateWeave && MCHBurstActionPolicy.ShouldUseWildfireAroundFullMetal(
					IsLastGCD(false, FullMetalFieldPvE),
					ShouldRecoverLostFullMetalBurst,
					WeaponRemain <= WildfireLateWeaveWindow,
					HasHeatForHypercharge))
				{
					if (WildfirePvE.CanUse(out act, skipTTKCheck: true))
					{
						if (WildfireResolvedTargetAllowed)
						{
							return true;
						}
					}
				}
			}
			if (!FullMetalFieldPvE.EnoughLevel)
			{
				if (HasHeatForHypercharge && ToolChargeSoon(out _) && !LowLevelHyperCheck)
				{
					if (WeaponRemain < (GCDTime(1) / 2))
					{
						if (WildfirePvE.CanUse(out act, skipTTKCheck: true))
						{
							if (WildfireResolvedTargetAllowed)
							{
								return true;
							}
						}
					}
				}
			}
		}

		// Queen outranks the charge actions for a weave slot. UseQueen returns false unless the
		// deploy decision already says yes, so this cannot deploy early - it only stops a ready
		// Queen from queueing behind Double Check and Checkmate, which have three charges each and
		// are refunded 15s per Blazing Shot, and so claim nearly every weave. That delay is not
		// free: the gauge keeps generating while the Queen waits, and any tool landing in the gap
		// is lost outright. Observed at 17:5x - gauge hit 100 at 377.3s, the Queen did not reach a
		// slot until 380.1s, and the Chain Saw at 379.7s overcapped for a full 20 Battery.
		// A displaced charge is at most 180 potency and has two more charges behind it; the Queen
		// it was delaying is 1184-2368.
		if (UseQueen(out act, nextGCD))
		{
			return true;
		}

		// Keep setup actions and charge pairs from becoming double/triple weaves.
		// LastAction resets on the next GCD, unlike the persistent LastAbility value.
		var canUseChargeAction = MCHBurstActionPolicy.ShouldUseChargeAction(
			IsLastAction(false, WildfirePvE, HyperchargePvE, ReassemblePvE,
				CheckmatePvE, RicochetPvE, DoubleCheckPvE, GaussRoundPvE));

		// Start Ricochet/Gauss cooldowns rolling after a released Wildfire gets its weave.
		if (canUseChargeAction && !RicochetPvE.Cooldown.IsCoolingDown)
		{
			if (CheckmatePvE.EnoughLevel && CheckmatePvE.CanUse(out act))
			{
				return true;
			}
			if (!CheckmatePvE.EnoughLevel && RicochetPvE.CanUse(out act))
			{
				return true;
			}
		}
		if (canUseChargeAction && !GaussRoundPvE.Cooldown.IsCoolingDown)
		{
			if (DoubleCheckPvE.EnoughLevel && DoubleCheckPvE.CanUse(out act))
			{
				return true;
			}
			if (!DoubleCheckPvE.EnoughLevel && GaussRoundPvE.CanUse(out act))
			{
				return true;
			}
		}

		// Use Hypercharge if wildfire will not be up in 30 seconds or if you hit 100 heat
		if ((!ShouldHoldFlexibleResources || Heat == 100) && !LowLevelHyperCheck && !HasReassembled
			&& (!WildfirePvE.Cooldown.WillHaveOneCharge(30) || ShouldRecoverLostFullMetalBurst || (Heat == 100)))
		{
			if (MCHBurstActionPolicy.ShouldStartFlexibleHypercharge(LiveComboTime, GCDTime(5))
				&& ToolChargeSoon(out act))
			{
				return true;
			}
		}

		// Decide which oGCD to use based on which has more RecastTimeElapsed
		var whichToUse = RicochetPvE.EnoughLevel switch
		{
			true when RicochetPvE.Cooldown.RecastTimeElapsed > GaussRoundPvE.Cooldown.RecastTimeElapsed => "Ricochet",
			true when GaussRoundPvE.Cooldown.RecastTimeElapsed > RicochetPvE.Cooldown.RecastTimeElapsed => "GaussRound",
			true => "Ricochet", // Default to Ricochet if equal
			_ => "GaussRound"
		};

		if (canUseChargeAction
			&& (!FullMetalFieldPvE.EnoughLevel || (FullMetalFieldPvE.EnoughLevel && !nextGCD.IsTheSameTo(false, FullMetalFieldPvE))))
		{
			switch (whichToUse)
			{
				case "Ricochet":
					if (CheckmatePvE.EnoughLevel && CheckmatePvE.CanUse(out act, usedUp: ShouldCommitPersonalBurst || IsOverheated))
					{
						return true;
					}
					if (!CheckmatePvE.EnoughLevel && RicochetPvE.CanUse(out act, usedUp: ShouldCommitPersonalBurst || IsOverheated))
					{
						return true;
					}
					break;
				case "GaussRound":
					if (DoubleCheckPvE.EnoughLevel && DoubleCheckPvE.CanUse(out act, usedUp: ShouldCommitPersonalBurst || IsOverheated))
					{
						return true;
					}
					if (!DoubleCheckPvE.EnoughLevel && GaussRoundPvE.CanUse(out act, usedUp: ShouldCommitPersonalBurst || IsOverheated))
					{
						return true;
					}
					break;
			}
		}

		return base.AttackAbility(nextGCD, out act);
	}
	#endregion

	#region GCD Logic
	protected override bool GeneralGCD(out IAction? act)
	{
		if (StatusHelper.PlayerWillStatusEnd(3, true, StatusID.FullMetalMachinist)
			&& FullMetalFieldPvE.CanUse(out act, skipTTKCheck: true))
		{
			return true;
		}

		// Once Reassemble is spent, consume it on a usable tool before combo preservation can steal it.
		IAction? reassembledAction = null;
		var reassembledTool = MCHBurstActionPolicy.SelectReassembledTool(HasReassembled, IsOverheated,
			tool => (ActionID)tool switch
			{
				ActionID.AirAnchorPvE => HotShotMasteryTrait.EnoughLevel && AirAnchorPvE.CanUse(out reassembledAction),
				ActionID.DrillPvE => DrillPvE.CanUse(out reassembledAction, usedUp: true),
				ActionID.ExcavatorPvE => ExcavatorPvE.CanUse(out reassembledAction, skipTTKCheck: true),
				ActionID.ChainSawPvE => ChainSawPvE.CanUse(out reassembledAction),
				_ => false,
			});
		if (reassembledTool != 0)
		{
			act = reassembledAction;
			return true;
		}

		// ensure combo is not broken, okay to drop during overheat
		if (IsLastComboAction(true, SlugShotPvE) && LiveComboTime >= GCDTime(1) && LiveComboTime <= GCDTime(2) && !IsOverheated)
		{
			// 3
			if (HeatedCleanShotPvE.EnoughLevel && HeatedCleanShotPvE.CanUse(out act))
			{
				return true;
			}
			if (!HeatedCleanShotPvE.EnoughLevel && CleanShotPvE.CanUse(out act))
			{
				return true;
			}
		}

		// ensure combo is not broken, okay to drop during overheat
		if (IsLastComboAction(true, SplitShotPvE) && LiveComboTime >= GCDTime(1) && LiveComboTime <= GCDTime(2) && !IsOverheated)
		{
			// 2
			if (HeatedSlugShotPvE.EnoughLevel && HeatedSlugShotPvE.CanUse(out act))
			{
				return true;
			}
			if (!HeatedSlugShotPvE.Info.EnoughLevelAndQuest() && SlugShotPvE.CanUse(out act))
			{
				return true;
			}
		}

		// Overheated AOE
		// Before level 92, Gauss Round is single-target, reducing the lost refund value.
		if (AutoCrossbowPvE.CanUse(out act) && AutoCrossbowPvE.Target.AffectedTargets.Length
			>= MCHBurstActionPolicy.AutoCrossbowTargetCount(DoubleCheckPvE.EnoughLevel && CheckmatePvE.EnoughLevel))
		{
			return true;
		}

		// Overheated ST
		if (BlazingShotPvE.EnoughLevel && BlazingShotPvE.CanUse(out act))
		{
			return true;
		}
		if (!BlazingShotPvE.EnoughLevel && HeatBlastPvE.CanUse(out act))
		{
			return true;
		}

		if (IsLastAction(false, HyperchargePvE) && HeatBlastPvE.EnoughLevel)
		{
			return base.GeneralGCD(out act);
		}

		// Drill AOE
		if ((BioMove || (!IsMoving && !BioMove)) && BioblasterPvE.CanUse(out act, usedUp: true))
		{
			return true;
		}

		// ST Big GCDs
		if (HotShotMasteryTrait.EnoughLevel && !ShouldHoldTool(AirAnchorPvE) && AirAnchorPvE.CanUse(out act))
		{
			return true;
		}

		// for opener: only use the first charge of Drill after AirAnchor when there are two
		if (!ShouldHoldTool(DrillPvE) && DrillPvE.CanUse(out act, usedUp: false))
		{
			return true;
		}

		if (!HotShotMasteryTrait.EnoughLevel && HotShotPvE.CanUse(out act))
		{
			return true;
		}

		// Use the combo finisher on sight unless holding it lands a scheduled Queen exactly. This
		// used to be unconditional, which also made the ExcavatorReady expiry check further down
		// unreachable - the delay mechanism was written and then defeated by this call.
		if (!ShouldHoldExcavatorForBattery && ExcavatorPvE.CanUse(out act, skipTTKCheck: true))
		{
			return true;
		}

		// ChainSaw is always used after Drill
		if (!ShouldHoldTool(ChainSawPvE) && ChainSawPvE.CanUse(out act))
		{
			return true;
		}

		if (ShouldUseFullMetalField
			&& !AirAnchorPvE.CanUse(out _) && !ChainSawPvE.CanUse(out _) && !ExcavatorPvE.CanUse(out _) && !HasExcavatorReady
			&& !IsLastGCD(false, ChainSawPvE) && DrillPvE.Cooldown.CurrentCharges < 2 && (!WildfirePvE.Cooldown.IsCoolingDown || IsLastAction(false, WildfirePvE)))
		{
			if (FullMetalFieldPvE.CanUse(out act, skipTTKCheck: true))
			{
				return true;
			}
		}

		if (!ShouldHoldFlexibleResources && !ShouldHoldTool(DrillPvE) && DrillPvE.CanUse(out act, usedUp: true))
		{
			return true;
		}

		if (StatusHelper.PlayerWillStatusEnd(3, true, StatusID.ExcavatorReady))
		{
			if (ExcavatorPvE.CanUse(out act, skipTTKCheck: true))
			{
				return true;
			}
		}

		// 1 AOE
		if (!IsOverheated)
		{
			if (ScattergunPvE.EnoughLevel)
			{
				if (ScattergunPvE.CanUse(out act))
				{
					return true;
				}
			}
			if (!ScattergunPvE.EnoughLevel)
			{
				if (SpreadShotPvE.CanUse(out act))
				{
					return true;
				}
			}
		}

		// 3 ST
		if (HeatedCleanShotPvE.EnoughLevel && HeatedCleanShotPvE.CanUse(out act))
		{
			return true;
		}
		if (!HeatedCleanShotPvE.EnoughLevel && CleanShotPvE.CanUse(out act))
		{
			return true;
		}
		// 2 ST
		if (HeatedSlugShotPvE.EnoughLevel && HeatedSlugShotPvE.CanUse(out act))
		{
			return true;
		}
		if (!HeatedSlugShotPvE.Info.EnoughLevelAndQuest() && SlugShotPvE.CanUse(out act))
		{
			return true;
		}
		// 1 ST
		if (HeatedSplitShotPvE.EnoughLevel && HeatedSplitShotPvE.CanUse(out act))
		{
			return true;
		}
		if (!HeatedSplitShotPvE.Info.EnoughLevelAndQuest() && SplitShotPvE.CanUse(out act))
		{
			return true;
		}

		return base.GeneralGCD(out act);
	}
	#endregion

	/// <summary>
	/// True when a tool GCD comes back inside the eight seconds a Hypercharge dump occupies, which
	/// is the window where starting one pushes that tool late.
	///
	/// Deliberately reads nothing but cooldowns. This used to live inline inside ToolChargeSoon,
	/// below its own ShouldUseHypercharge early-return, which made ToolChargeSoon the only path
	/// that could see it - and ToolChargeSoon guards exactly one of the six Hypercharge call sites.
	/// The other five nest directly inside ShouldUseHypercharge and were unguarded. Pulling the
	/// cooldown test out here gives that property something it can read without calling back into
	/// ToolChargeSoon, which would recurse through the early-return.
	/// </summary>
	protected bool ToolChargeWithinRestWindow
	{
		get
		{
			const float REST_TIME = 8f;
			// Cannot AOE: on AoE targeting the single-target tools are not the priority this
			// window protects, so nothing is worth delaying Hypercharge for.
			if (SpreadShotPvE.CanUse(out _)) return false;

			// Disabled or unlearned tools cannot use the protected GCD slot.
			return MCHBurstActionPolicy.ShouldProtectTool(
				AirAnchorPvE.IsEnabled, AirAnchorPvE.Info.EnoughLevelAndQuest(), AirAnchorPvE.Cooldown.WillHaveOneCharge(REST_TIME))
				|| (!AirAnchorPvE.EnoughLevel && MCHBurstActionPolicy.ShouldProtectTool(
					HotShotPvE.IsEnabled, HotShotPvE.Info.EnoughLevelAndQuest(), HotShotPvE.Cooldown.WillHaveOneCharge(REST_TIME)))
				|| MCHBurstActionPolicy.ShouldProtectTool(
					DrillPvE.IsEnabled, DrillPvE.Info.EnoughLevelAndQuest(), DrillPvE.Cooldown.WillHaveXCharges(DrillPvE.Cooldown.MaxCharges, REST_TIME))
				|| MCHBurstActionPolicy.ShouldProtectTool(
					ChainSawPvE.IsEnabled, ChainSawPvE.Info.EnoughLevelAndQuest(), ChainSawPvE.Cooldown.WillHaveOneCharge(REST_TIME));
		}
	}

	// Logic for Hypercharge
	private bool ToolChargeSoon(out IAction? act)
	{
		if (!ShouldUseHypercharge || ToolChargeWithinRestWindow)
		{
			act = null;
			return false;
		}

		return HyperchargePvE.CanUse(out act, skipTTKCheck: true);
	}

	protected bool UseQueen(out IAction? act, IAction nextGCD, bool onlyToAvoidOvercap = false)
	{
		act = null;
		if (!InCombat || IsRobotActive)
		{
			return false;
		}

		// The selected GCD is authoritative, including explicit commands. Speculative generators
		// must be enabled and learned before they can justify spending Battery early.
		var airAnchorSoon = nextGCD.IsTheSameTo(false, HotShotPvE, AirAnchorPvE)
			|| (AirAnchorPvE.EnoughLevel
				? MCHBurstActionPolicy.ShouldProtectTool(AirAnchorPvE.IsEnabled,
					AirAnchorPvE.Info.EnoughLevelAndQuest(), AirAnchorPvE.Cooldown.WillHaveOneCharge(GCDTime(2)))
				: MCHBurstActionPolicy.ShouldProtectTool(HotShotPvE.IsEnabled,
					HotShotPvE.Info.EnoughLevelAndQuest(), HotShotPvE.Cooldown.WillHaveOneCharge(GCDTime(2))));
		var chainSawSoon = nextGCD.IsTheSameTo(false, ChainSawPvE)
			|| MCHBurstActionPolicy.ShouldProtectTool(ChainSawPvE.IsEnabled,
				ChainSawPvE.Info.EnoughLevelAndQuest(), ChainSawPvE.Cooldown.WillHaveOneCharge(GCDTime(2)));
		var excavatorReady = nextGCD.IsTheSameTo(false, ExcavatorPvE)
			|| MCHBurstActionPolicy.ShouldProtectTool(ExcavatorPvE.IsEnabled,
				ExcavatorPvE.Info.EnoughLevelAndQuest(), HasExcavatorReady);
		// Chain Saw is worth its own 20 here. The Excavator it grants is a separate, later GCD and
		// reaches the projection through excavatorReady when it is actually ready.
		var projectedBatteryGain = MCHBatteryPolicy.ProjectedBatteryGain(
			nextGCD.IsTheSameTo(false, CleanShotPvE, HeatedCleanShotPvE),
			airAnchorSoon,
			chainSawSoon ? 20 : 0,
			excavatorReady);
		if (onlyToAvoidOvercap && !MCHBatteryPolicy.WouldOvercap(Battery, projectedBatteryGain)) return false;
		// A hard hold is an encounter/schedule invariant, so it must win before the generic
		// overcap escape inside MCHBatteryPolicy. Treating it as an ordinary hold let a projected
		// generator deploy a low-Battery Queen even when Sync+ was explicitly reserving the gauge.
		// It is handed the projection so a hold that only exists to reach a higher Battery target
		// can stand down rather than cap the gauge, which reserves nothing and loses the surplus.
		if (ShouldHardHoldQueen(projectedBatteryGain)) return false;

		var queenHeld = ShouldHoldQueen;
		// The virtual, not a second inline copy of the predicate. This local used to duplicate the
		// derived rotation's opener test with its own stale bounds, so fixing one left the other
		// dead - and for the base rotation, where ShouldReleaseQueenAtMinimumBattery is false, this
		// was the only opener path there was.
		var openerRelease = QueenOpenerRelease;
		var deployBattery = Battery;
		if (MCHBatteryPolicy.ShouldDeployQueen(
			Battery,
			projectedBatteryGain,
			queenHeld,
			ShouldReleaseQueenAtMinimumBattery,
			openerRelease)
			&& TryDeployQueen(out act))
		{
			OnQueenDeployed(deployBattery);
			return true;
		}
		return false;
	}

	/// <summary>
	/// The TTK guard stays on, including at a full gauge. Skipping it at 100 Battery was tried once
	/// and reverted the same session: it cost more than it saved. The stall it was meant
	/// to fix bled 40 Battery over 10.4s, but without it a Lindwurm pull dumped a full
	/// 100-Battery Queen into a ~14s untargetable phase at 5:21 for literally 0 damage, and another
	/// at 6:36 with 1.8s left in the fight. On this encounter the downtime guard cannot help -
	/// the BossMod module loads but reports "downtime in: unknown" - so TTK is the only backstop
	/// against deploying into downtime, and a wasted Queen is dearer than a capped gauge.
	/// </summary>
	private bool TryDeployQueen(out IAction? act)
		=> AutomatonQueenPvE.EnoughLevel
			? AutomatonQueenPvE.CanUse(out act)
			: RookAutoturretPvE.CanUse(out act);
}

internal static class MCHMitigationPolicy
{
	internal static bool ShouldUseTactician(bool restrictOverlap, bool dismantleActive)
		=> !restrictOverlap || !dismantleActive;

	internal static bool ShouldUseDismantle(bool restrictOverlap, bool rangedMitigationActive)
		=> !restrictOverlap || !rangedMitigationActive;
}

internal static class MCHBurstActionPolicy
{
	internal static uint SelectReassembledTool(bool hasReassembled, bool isOverheated, Func<uint, bool> canUse)
	{
		if (!hasReassembled || isOverheated) return 0;
		if (canUse((uint)ActionID.AirAnchorPvE)) return (uint)ActionID.AirAnchorPvE;
		if (canUse((uint)ActionID.DrillPvE)) return (uint)ActionID.DrillPvE;
		if (canUse((uint)ActionID.ExcavatorPvE)) return (uint)ActionID.ExcavatorPvE;
		if (canUse((uint)ActionID.ChainSawPvE)) return (uint)ActionID.ChainSawPvE;
		return 0;
	}

	internal static bool ShouldProtectTool(bool enabled, bool learned, bool chargeReadySoon)
		=> enabled && learned && chargeReadySoon;

	internal static int AutoCrossbowTargetCount(bool upgradedShots) => upgradedShots ? 6 : 3;

	/// <summary>Enemies in a Chain Saw line (25y long, 4y wide) from the player through the aim point, hitboxes included.</summary>
	internal static int LineHits(System.Numerics.Vector2 player, System.Numerics.Vector2 aim, IEnumerable<(System.Numerics.Vector2 Pos, float Radius)> enemies)
	{
		var dir = aim - player;
		if (dir.LengthSquared() < 0.0001f) return 0;
		dir = System.Numerics.Vector2.Normalize(dir);
		return enemies.Count(e =>
		{
			var d = e.Pos - player;
			var along = System.Numerics.Vector2.Dot(d, dir);
			return along >= -e.Radius && along - e.Radius <= 25f && MathF.Abs(d.X * dir.Y - d.Y * dir.X) <= 2f + e.Radius;
		});
	}
	internal static bool ShouldUseWildfireAroundFullMetal(
		bool lastGcdWasFullMetal,
		bool recoverLostFullMetalBurst, bool lateWeave, bool hasHeatForWildfire)
		=> lateWeave && hasHeatForWildfire
			&& (lastGcdWasFullMetal || recoverLostFullMetalBurst);

	internal static bool ShouldDumpHeatBeforeDowntime(
		bool enabled,
		bool downtimeSoon,
		bool hasWildfire,
		bool isOverheated,
		bool hasHeatResource,
		bool isPreBurst,
		float wildfireReadyIn,
		float downtimeIn,
		bool holdFlexibleResources)
		=> enabled && downtimeSoon && !hasWildfire && !isOverheated
			&& hasHeatResource
			&& !(isPreBurst && wildfireReadyIn <= downtimeIn)
			&& !holdFlexibleResources;

	internal static bool ShouldForceCappedHypercharge(
		bool heatCapped, bool hasWildfire, bool hasFullMetalMachinist)
		=> heatCapped && !(hasWildfire && hasFullMetalMachinist);

	internal static bool ShouldStartWildfireHypercharge(bool hasWildfire, bool hasFullMetalMachinist)
		=> hasWildfire && !hasFullMetalMachinist;

	internal static bool ShouldSpendReassembleOnDrill(bool chargesCapped, bool spendRemainingCharge, bool nextGcdIsDrill)
		=> (chargesCapped || spendRemainingCharge) && nextGcdIsDrill;

	internal static bool ShouldUseChargeAction(bool lastActionBlocksCharge)
		=> !lastActionBlocksCharge;

	internal static bool ShouldStartFlexibleHypercharge(float liveComboTime, float safetyWindow)
		=> liveComboTime <= 0f || liveComboTime > safetyWindow;
}

internal static class MCHBatteryPolicy
{
	/// <summary>
	/// The largest SINGLE imminent Battery gain, not the sum of everything within a couple of
	/// GCDs. Callers use this to answer "will the gauge cap before I can deploy?", and that
	/// question is about the next generator only - the ones after it are each preceded by another
	/// chance to deploy, so folding them in claims an overcap that cannot happen.
	///
	/// Summing them cost real Queens: at 50 Battery with Chain Saw and Air Anchor both near, the
	/// old sum read 110 and force-deployed a 50-Battery Queen, while the gauge actually climbed
	/// +20/+20/+10 over the next twelve seconds and reached exactly 100.
	///
	/// Excavator is also counted once. It used to arrive both bundled into chainSawGain (40 =
	/// Chain Saw plus the Excavator it grants) and again through excavatorReady.
	/// </summary>
	internal static int ProjectedBatteryGain(
		bool tenBatteryGcdNext,
		bool airAnchorSoon,
		int chainSawGain,
		bool excavatorReady)
		=> Math.Max(
			Math.Max(tenBatteryGcdNext ? 10 : 0, airAnchorSoon ? 20 : 0),
			Math.Max(chainSawGain, excavatorReady ? 20 : 0));

	internal static bool WouldOvercap(int battery, int projectedBatteryGain)
		=> projectedBatteryGain > 0 && battery + projectedBatteryGain > 100;

	internal static bool ShouldDeployQueen(
		byte battery,
		int projectedBatteryGain,
		bool queenHeld,
		bool burstWindow,
		bool openerRelease)
	{
		if (battery < 50) return false;
		if (WouldOvercap(battery, projectedBatteryGain)) return true;
		if (queenHeld) return false;
		return openerRelease || burstWindow || battery >= 90;
	}
}
