namespace RotationSolver.RebornRotations.PVPRotations.Ranged;

[Rotation("Default PVP", CombatType.PvP, GameVersion = "7.55")]
[SourceCode(Path = "main/RebornRotations/PVPRotations/Ranged/BRD_Default.PvP.cs")]

public sealed class BRD_DefaultPvP : BardRotation
{
	#region Configurations

	[RotationConfig(CombatType.PvP, Name = "Use Warden's Paean on other players")]
	public bool BRDEsuna2 { get; set; } = false;

	[Range(0, 1, ConfigUnitType.Percent)]
	[RotationConfig(CombatType.PvP, Name = "Dump all Harmonic Arrow charges when the target's HP is at or below")]
	public float HarmonicExecutePercent { get; set; } = 0.4f;

	[Range(0, 1, ConfigUnitType.Percent)]
	[RotationConfig(CombatType.PvP, Name = "Use Bravery for its mitigation when your HP is below")]
	public float BraveryDefensivePercent { get; set; } = 0.5f;
	#endregion

	#region oGCDs
	protected override bool EmergencyAbility(IAction nextGCD, out IAction? action)
	{
		if (BRDEsuna2 && TheWardensPaeanPvP.CanUse(out action))
		{
			return true;
		}
		if (StatusHelper.PlayerHasStatus(false, StatusHelper.PurifyPvPStatuses))
		{
			if (TheWardensPaeanPvP.CanUse(out action, targetOverride: TargetType.Self))
			{
				return true;
			}
		}

		if (PurifyPvP.CanUse(out action))
		{
			if (Service.Config.PvpPurifyStun && StatusHelper.PlayerHasStatus(false, StatusID.Stun_1343))
			{
				return true;
			}

			if (Service.Config.PvpPurifyHeavy && StatusHelper.PlayerHasStatus(false, StatusID.Heavy_1344))
			{
				return true;
			}

			if (Service.Config.PvpPurifyBind && StatusHelper.PlayerHasStatus(false, StatusID.Bind_1345))
			{
				return true;
			}

			if (Service.Config.PvpPurifySilence && StatusHelper.PlayerHasStatus(false, StatusID.Silence_1347))
			{
				return true;
			}

			if (Service.Config.PvpPurifyDeepFreeze && StatusHelper.PlayerHasStatus(false, StatusID.DeepFreeze_3219))
			{
				return true;
			}

			if (Service.Config.PvpPurifyMiracleOfNature && StatusHelper.PlayerHasStatus(false, StatusID.MiracleOfNature))
			{
				return true;
			}
		}

		// Bravery: hold for the big hits (or to survive), since it lasts 15s on a 30s recast.
		if (InCombat && BraveryPvP.CanUse(out action))
		{
			if (Player?.GetHealthRatio() < BraveryDefensivePercent
				|| nextGCD.IsTheSameTo(false, ActionID.HarmonicArrowPvP, ActionID.BlastArrowPvP, ActionID.PitchPerfectPvP))
			{
				return true;
			}
		}

		if (DervishPvP.CanUse(out action))
		{
			if (InCombat)
			{
				return true;
			}
		}

		return base.EmergencyAbility(nextGCD, out action);
	}

	protected override bool AttackAbility(IAction nextGCD, out IAction? action)
	{
		// Nocturne first: same Repertoire, no 10y backstep.
		if (SilentNocturnePvP.CanUse(out action))
		{
			if (!StatusHelper.PlayerHasStatus(true, StatusID.Repertoire))
			{
				return true;
			}
		}

		if (RepellingShotPvP.CanUse(out action))
		{
			if (!StatusHelper.PlayerHasStatus(true, StatusID.Repertoire))
			{
				return true;
			}
		}

		if (EagleEyeShotPvP.CanUse(out action))
		{
			return true;
		}

		if (EncoreOfLightPvP.CanUse(out action, skipAoeCheck: true))
		{
			return true;
		}

		return base.AttackAbility(nextGCD, out action);
	}
	#endregion

	#region GCDs
	protected override bool GeneralGCD(out IAction? action)
	{
		if (!StatusHelper.PlayerHasStatus(true, StatusID.FrontlinersMarch))
		{
			if (ApexArrowPvP.CanUse(out action, skipStatusProvideCheck: true))
			{
				return true;
			}
		}

		if (HarmonicArrowPvP.CanUse(out action))
		{
			return true;
		}

		// Extra Harmonic charges add less per charge, but the GCDs they save are Powerful Shots,
		// so total damage is the same either way. Dumping early on a low target costs nothing.
		if (HarmonicArrowPvP.CanUse(out action, usedUp: true) && HarmonicArrowPvP.Target.Target.GetHealthRatio() <= HarmonicExecutePercent)
		{
			return true;
		}

		if (PitchPerfectPvP.CanUse(out action, skipAoeCheck: true))
		{
			return true;
		}

		if (BlastArrowPvP.CanUse(out action))
		{
			return true;
		}

		if (ApexArrowPvP.CanUse(out action, skipStatusProvideCheck: true))
		{
			return true;
		}

		if (PowerfulShotPvP.CanUse(out action))
		{
			return true;
		}

		return base.GeneralGCD(out action);
	}
	#endregion
}