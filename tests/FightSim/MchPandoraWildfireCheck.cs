using System.Reflection;
using RotationSolver.Basic.Actions;
using RotationSolver.Basic.Data;
using RotationSolver.RebornRotations.Ranged;
using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

internal static class MchPandoraWildfireCheck
{
	public static void Run()
	{
		var checks = 0;
		foreach (var mode in new[] { "ready", "disabled", "short target", "no buffs", "P1", "P2" })
		{
			var id = mode == "P1" ? 0x459Bu : mode == "P2" ? 0x459Fu : 0x45AFu;
			var rotation = (MCH_Plan)typeof(MchQueenDecisionCheck)
				.GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static)!
				.Invoke(null, [id, 983f, true, 1f, null])!;
			if (mode != "no buffs") Game.Timeline.PartyBuffs.Add(new("Starry Muse", "PCT", 1.05f, 980, 1000));
			Game.TtkOverride = mode == "short target" ? 3 : 100;
			Game.Cooldown(BarrelStabilizerPvE).FullAt = 992; // Too far away to hold the live Wildfire for Barrel.
			Game.Cooldown(WildfirePvE).FullAt = 0;
			MCH_Reborn.WildfirePvE.Config.IsEnabled = mode != "disabled";
			MCH_Reborn.HeatedSlugShotPvE.CanUse(out var shot, skipComboCheck: true);
			Game.Execute(shot);
			Machinist.Gauge.Heat = 50;
			Game.Step(.68f);
			rotation.NextGcd();
			var early = rotation.NextAbility(MCH_Reborn.HeatedCleanShotPvE);
			if (mode == "ready")
			{
				Require(early?.ID != (uint)HyperchargePvE, "early Hypercharge must reserve the executable fallback Wildfire weave");
				Game.Step(.84f);
				var late = rotation.NextAbility(MCH_Reborn.HeatedCleanShotPvE);
				Require(late?.ID == (uint)WildfirePvE, "late weave executes Wildfire without an FMF token");
				Game.Execute(late!);
				Game.Step(.7f);
				Game.Step(Game.GcdRemain);
				Game.Execute(rotation.NextGcd()!);
				Game.Step(.7f);
				Require(rotation.NextAbility(MCH_Reborn.BlazingShotPvE)?.ID == (uint)HyperchargePvE,
					"Wildfire releases its Hypercharge payload");
			}
			else if (mode is "disabled" or "P1" or "P2")
				Require(early?.ID == (uint)HyperchargePvE, mode + " retains the prior flexible Hypercharge selection");
			else Require(!(bool)typeof(MCH_SyncPlus).GetProperty("PendingWildfireAroundFullMetal", BindingFlags.NonPublic | BindingFlags.Instance)!
				.GetValue(rotation)!, mode + " cannot reserve a fallback Wildfire weave");
			checks++;
		}
		Console.WriteLine($"Pandora fallback Wildfire: {checks} selector scenarios passed.");
	}

	private static void Require(bool value, string message)
	{
		if (!value) throw new InvalidOperationException(message);
	}
}
