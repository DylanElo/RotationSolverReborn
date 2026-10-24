using System.Reflection;
using RotationSolver.Basic.Actions;
using RotationSolver.RebornRotations.Ranged;
using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

internal static class MchPandoraBuffQueenCheck
{
	public static void Run()
	{
		var noGcd = BaseAction.Create(None, (ref ActionSetting _) => { });
		var checks = 0;
		foreach (var mode in new[] { "ready", "anchor", "saw", "excavator", "disabled anchor", "P4", "no buffs", "early buffs", "robot", "70 battery", "short lifetime" })
		{
			var id = mode == "P4" ? 0x45A9u : 0x45AFu;
			var rotation = (MCH_Plan)typeof(MchQueenDecisionCheck).GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static)!
				.Invoke(null, [id, 100f, true, 1f, null])!;
			// P5 follows confirmed earlier Queens; this is not the fresh-fight 60-Battery opener.
			typeof(MCH_SyncPlus).GetField("_queenDeployedThisFight", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(rotation, true);
			if (mode != "no buffs") Game.Timeline.PartyBuffs.Add(new("Starry Muse", "PCT", 1.05f, 99, mode == "early buffs" ? 125 : 115));
			Game.TtkOverride = mode == "short lifetime" ? 2 : 100;
			Machinist.Gauge.Battery = (byte)(mode == "70 battery" ? 70 : 80);
			if (mode == "robot") Machinist.Gauge.SummonEnd = 110;
			if (mode is "anchor" or "disabled anchor") Game.Cooldown(AirAnchorPvE).FullAt = 0;
			if (mode == "saw") Game.Cooldown(ChainSawPvE).FullAt = 0;
			if (mode == "excavator") Game.Player.Statuses[RotationSolver.Basic.Data.StatusID.ExcavatorReady] = 20;
			if (mode == "disabled anchor") MCH_Reborn.AirAnchorPvE.Config.IsEnabled = false;
			Game.Step(.01f);
			rotation.NextGcd();
			var selected = rotation.NextAbility(noGcd);
			var expected = mode is "ready" or "disabled anchor";
			if ((selected?.ID == (uint)AutomatonQueenPvE) != expected)
				throw new InvalidOperationException(mode + ": live Pandora buffs release 80 Battery only without an imminent usable generator and with enough lifetime");
			checks++;
		}
		Console.WriteLine($"Pandora partial buff Queen: {checks} selector scenarios passed.");
	}
}
