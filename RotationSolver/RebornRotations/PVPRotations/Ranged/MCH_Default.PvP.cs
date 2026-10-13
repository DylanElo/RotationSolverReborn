namespace RotationSolver.RebornRotations.PVPRotations.Ranged;

[Rotation("Default PVP", CombatType.PvP, GameVersion = "7.55")]
[SourceCode(Path = "main/RebornRotations/PVPRotations/Ranged/MCH_Default.PvP.cs")]

public sealed class MCH_DefaultPvP : MachinistRotation
{
	#region Configurations
	[Range(0, 1, ConfigUnitType.Percent)]
	[RotationConfig(CombatType.PvP, Name = "Use Bravery for its mitigation when your HP is below")]
	public float BraveryDefensivePercent { get; set; } = 0.5f;
	#endregion

	private static bool HasOverheatedPvP => StatusHelper.PlayerHasStatus(true, StatusID.Overheated_3149);

	#region oGCDs
	protected override bool EmergencyAbility(IAction nextGCD, out IAction? action)
	{
		// Bravery: cover the Overheated window (Full Metal Field / Blazing Shots / Wildfire), or survive.
		if (InCombat && BraveryPvP.CanUse(out action))
		{
			if (HasOverheatedPvP
				|| nextGCD.IsTheSameTo(false, ActionID.FullMetalFieldPvP)
				|| Player?.GetHealthRatio() < BraveryDefensivePercent)
			{
				return true;
			}
		}

		if (InCombat && DervishPvP.CanUse(out action))
		{
			return true;
		}

		return base.EmergencyAbility(nextGCD, out action);
	}

	protected override bool AttackAbility(IAction nextGCD, out IAction? action)
	{
		// Analysis is scarce (2 charges, 20s) and the tools come up nearly every GCD.
		// Skip Bioblaster: +50% only adds ~4,000 total, spread over a 12s DoT.
		// Drill doubles (+10,000, ignores Guard), Air Anchor's Bind becomes a Stun, and Chain Saw adds a 20% vuln.
		if (nextGCD.IsTheSameTo(false, ActionID.DrillPvP, ActionID.AirAnchorPvP, ActionID.ChainSawPvP))
		{
			if (AnalysisPvP.CanUse(out action, usedUp: true))
			{
				return true;
			}
		}

		// Wildfire gains 4,500 per hit and detonates at 4 hits. Overheated Blazing Shots (1.5s) land 4 well
		// inside the 8s mark, and placing it before Full Metal Field makes FMF count as a hit too.
		if (WildfirePvP.CanUse(out action))
		{
			if (HasOverheatedPvP || nextGCD.IsTheSameTo(false, ActionID.FullMetalFieldPvP))
			{
				return true;
			}
		}

		if (BishopAutoturretPvP.CanUse(out action))
		{
			return true;
		}

		if (EagleEyeShotPvP.CanUse(out action))
		{
			return true;
		}

		return base.AttackAbility(nextGCD, out action);
	}

	#endregion

	#region GCDs
	protected override bool GeneralGCD(out IAction? action)
	{
		if (FullMetalFieldPvP.CanUse(out action))
		{
			return true;
		}

		if (BlazingShotPvP.CanUse(out action))
		{
			if (HasOverheatedPvP && !StatusHelper.PlayerHasStatus(true, StatusID.Analysis))
			{
				return true;
			}
		}

		if (DrillPvP.CanUse(out action, usedUp: true))
		{
			return true;
		}

		if (BioblasterPvP.CanUse(out action, usedUp: true))
		{
			return true;
		}

		if (AirAnchorPvP.CanUse(out action, usedUp: true))
		{
			return true;
		}

		if (ChainSawPvP.CanUse(out action, usedUp: true))
		{
			return true;
		}

		if (ScattergunPvP.CanUse(out action))
		{
			if (!HasOverheatedPvP)
			{
				return true;
			}
		}

		if (BlastChargePvP.CanUse(out action))
		{
			return true;
		}

		return base.GeneralGCD(out action);
	}
	#endregion
}