using System.Reflection;
using RotationSolver.Basic.Data;
using RotationSolver.RebornRotations.Ranged;
using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

internal static class MchPandoraFinalFieldCheck
{
	public static void Run()
	{
		var checks = 0;
		foreach (var mode in new[] { "final", "healthy", "P4", "disabled", "unknown recent" })
		{
			var id = mode == "P4" ? 0x45A9u : 0x45AFu;
			var actor = new TimelineActor("boss", id, "boss", [[0, 2000]],
				[[100, mode == "unknown recent" ? .6f : .96f], [103, .6f]], [[0, 100, 100]], null);
			var timeline = new Timeline("final Pandora FMF", "MCH", 2000, [actor], [[0, 100, 100]], [],
				[new("Starry Muse", "PCT", 1.05f, 99, 120)], [], [], [], 0, [], [], [], "MCH", ["MCH", "PCT"], []);
			var rotation = (MCH_Plan)typeof(MchQueenDecisionCheck).GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static)!
				.Invoke(null, [id, 100f, true, 1f, timeline])!;
			Game.TtkOverride = mode == "healthy" ? 100 : 8;
			Machinist.Gauge.Heat = 50;
			MCH_Reborn.HyperchargePvE.CanUse(out var hypercharge, skipTTKCheck: true);
			Game.Execute(hypercharge);
			Game.Step(.7f);
			MCH_Reborn.BlazingShotPvE.CanUse(out var shot, skipTTKCheck: true);
			Game.Execute(shot);
			Game.Step(103 - Game.Now);
			Game.Player.Statuses[StatusID.FullMetalMachinist] = 25;
			Game.Player.Statuses[StatusID.Wildfire_1946] = 8;
			MCH_Reborn.FullMetalFieldPvE.Config.IsEnabled = mode != "disabled";
			Require(MCH_Reborn.OverheatedStacks == 4, "fixture retains four Blazing Shots");
			Require(Math.Abs(MCH_Reborn.BlazingShotPvE.Cooldown.RecastTimeOneChargeRaw - 1.5f) < .01f,
				"Blazing Shot uses its actual 1.5-second recast");
			var selected = rotation.NextGcd();
			Require(selected?.ID == (uint)(mode == "final" ? FullMetalFieldPvE : BlazingShotPvE),
				mode + " selects a field only when the final target cannot survive all remaining Blazing Shots");
			checks++;
		}
		Console.WriteLine($"Pandora final Full Metal Field: {checks} selector scenarios passed.");
	}

	private static void Require(bool value, string message)
	{
		if (!value) throw new InvalidOperationException(message);
	}
}
