using FightSim;
using RotationSolver.Basic.Actions;
using RotationSolver.Basic.Data;
using RotationSolver.Basic.Helpers;

namespace RotationSolver.Basic.Rotations.Basic;

/// <summary>The role-action settings CustomRotation_Actions gives the rotations' Swiftcast and Lucid Dreaming (the fake CustomRotation has none).</summary>
public partial class BlackMageRotation
{
	static partial void ModifySwiftcastPvE(ref ActionSetting setting)
	{
		setting.StatusProvide = StatusHelper.SwiftcastStatus;
		setting.IsFriendly = true;
	}

	static partial void ModifyLucidDreamingPvE(ref ActionSetting setting)
	{
		setting.ActionCheck = () => BlackMage.Mp < 6000f && Game.InCombat;
		setting.IsFriendly = true;
	}
}
