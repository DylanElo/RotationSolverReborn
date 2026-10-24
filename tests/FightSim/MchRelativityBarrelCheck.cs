using System.Reflection;
using RotationSolver.Basic.Actions;
using RotationSolver.Basic.Data;
using RotationSolver.RebornRotations.Ranged;
using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

internal static class MchRelativityBarrelCheck
{
	public static void Run()
	{
		var checks = 0;
		foreach (var mode in new[] { "ready", "zero return", "unknown return", "P1", "P2" })
		{
			var id = mode == "P1" ? 0x459Bu : mode == "P2" ? 0x459Fu : 0x45A7u;
			var rotation = (MCH_Plan)typeof(MchQueenDecisionCheck)
				.GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static)!
				.Invoke(null, [id, 490.76882f, true, 1f, null])!;
			Game.Timeline.PlanWindows.Add(new(496.44882f, 516.44882f, false));
			Game.Timeline.PartyBuffs.Add(new("Starry Muse", "PCT", 1.05f, 496.44882f, 516.44882f));
			Game.TtkOverride = 250;
			Game.Cooldown(BarrelStabilizerPvE).FullAt = 491.07462f;
			Game.Cooldown(WildfirePvE).FullAt = 496.44882f;
			MCH_Reborn.HeatedSplitShotPvE.CanUse(out var shot, skipComboCheck: true);
			Require(shot != null, "fixture has an executable pre-UR GCD");
			Game.Execute(shot!);
			Game.Step(.68f);
			Game.ForecastOverride = (.49f, mode == "zero return" ? 0f : mode == "unknown return" ? float.MaxValue : 5f);
			Require(!Game.Locked && Game.Cooldown(BarrelStabilizerPvE).FullAt <= Game.Now,
				"fixture reaches a legal pre-Stun weave with Barrel ready");
			rotation.NextGcd();
			var selected = rotation.NextAbility(MCH_Reborn.HeatedSlugShotPvE);
			Require((selected?.ID == (uint)BarrelStabilizerPvE) == (mode == "ready"),
				mode + ": only a known positive P3 return prepares Barrel in the final half-second before UR");
			if (mode == "ready")
			{
				Game.Execute(selected!);
				Require(Game.Player.Statuses.GetValueOrDefault(StatusID.FullMetalMachinist) > 5,
					"the executed Barrel token survives the forecast return and planned burst");
			}
			checks++;
		}
		Console.WriteLine($"Relativity final-weave Barrel: {checks} selector scenarios passed.");
	}

	private static void Require(bool value, string message)
	{
		if (!value) throw new InvalidOperationException(message);
	}
}
