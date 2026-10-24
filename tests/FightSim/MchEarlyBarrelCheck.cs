using System.Reflection;
using RotationSolver.Basic.Actions;
using RotationSolver.Basic.Data;
using RotationSolver.RebornRotations.Ranged;
using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

internal static class MchEarlyBarrelCheck
{
	public static void Run()
	{
		var noGcd = BaseAction.Create(None, (ref ActionSetting _) => { });
		var failures = new List<string>();
		var checks = 0;
		foreach (var phase in new uint[] { 0x459B, 0x459F, 0x45A3, 0x45AF })
		foreach (var mode in new[] { "prepare", "open window", "20 seconds", "no AutoBurst", "outside FRU", "unknown burst", "too early", "dying", "unsafe downtime", "downtime before burst", "death before burst" })
		{
			var rotation = Reset(phase);
			var burstIn = mode == "open window" ? 0f : mode is "20 seconds" or "downtime before burst" or "death before burst" ? 20f : mode == "too early" ? 20.1f : 10f;
			if (mode != "unknown burst") Game.Timeline.PlanWindows.Add(new(100 + burstIn, 120 + burstIn, false));
			Game.Cooldown(BarrelStabilizerPvE).FullAt = 0;
			Game.AutoBurst = mode != "no AutoBurst";
			Game.IsInFRU = mode != "outside FRU";
			Game.TtkOverride = mode == "dying" ? 2 : 250;
			if (mode == "unsafe downtime") Game.ForecastOverride = (2f, 40f);
			if (mode is "downtime before burst" or "death before burst") Game.Cooldown(WildfirePvE).FullAt = 0;
			if (mode == "downtime before burst") Game.ForecastOverride = (15f, 50f);
			if (mode == "death before burst") Game.TtkOverride = 15f;
			rotation.NextGcd();
			var selected = rotation.NextAbility(noGcd);
			// Pandora's Barrel waits for live buffs (4s into the burst) instead of preparing ahead of them.
			var expected = phase != 0x45AF && mode is "prepare" or "open window" or "20 seconds";
			// Existing no-plan fallback may commit Barrel; the new preparation must not grant permission itself.
			if (mode == "unknown burst" || phase == 0x45AF && mode == "death before burst")
				Check(!(bool)typeof(MCH_SyncPlus).GetProperty("PrepareBarrelForBurst", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rotation)!,
					$"{phase:X}: unknown timing does not activate early preparation");
			else
				Check((selected?.ID == (uint)BarrelStabilizerPvE) == expected,
					$"{phase:X} {mode}: prepare only for a known safe burst within 20 seconds");
			if (expected && selected?.ID == (uint)BarrelStabilizerPvE)
			{
				Game.Execute(selected);
				Game.Step(burstIn);
				Check(Game.Player.Statuses.GetValueOrDefault(StatusID.FullMetalMachinist) >= 9.9f,
					$"{phase:X} {mode}: the actual token retains payload slack at the planned burst");
			}
		}

		foreach (var mode in new[] { "confirmed anchor", "missing anchor", "too early", "expired", "unknown burst", "no AutoBurst" })
		{
			var actor = new TimelineActor("shiva", 0x459F, "P2", [[0, 100.1f]], [[0, .19f]], [[0, 100, 100]], null);
			var fixture = new Timeline("early Barrel observed crystal return", "MCH", 2000, [actor], [[0, 100, 100]], [], [], [], [], [], 0,
				mode == "missing anchor" ? [] : [new("shiva", 40224u, 100, 3)], [], [], "MCH", ["MCH", "PCT"], []);
			var rotation = (MCH_Plan)typeof(MchQueenDecisionCheck).GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static)!
				.Invoke(null, [0x459Fu, 100f, true, .19f, fixture])!;
			if (mode != "unknown burst") Game.Timeline.PlanWindows.Add(new(125, 145, false));
			rotation.NextAbility(noGcd); // Observe Absolute Zero while it is still casting.
			Game.Step(3.1f);
			rotation.NextAbility(noGcd); // Observe its confirmed completion and establish the actual return anchor.
			Game.Step((mode == "too early" ? 103.2f : mode == "expired" ? 126f : 110f) - Game.Now);
			Game.ForecastOverride = (float.MaxValue, 999f); // Generic forecast cannot supply the crystal return.
			Game.AutoBurst = mode != "no AutoBurst";
			Game.Cooldown(BarrelStabilizerPvE).FullAt = 0;
			var barrel = rotation.NextAbility(noGcd);
			Check((barrel?.ID == (uint)BarrelStabilizerPvE) == (mode == "confirmed anchor"),
				$"crystal {mode}: only a confirmed near return and near burst prepare Barrel during downtime");
			if (mode == "confirmed anchor" && barrel?.ID == (uint)BarrelStabilizerPvE)
			{
				Game.Execute(barrel);
				Game.Step(15f);
				Check(Game.Player.Statuses.GetValueOrDefault(StatusID.FullMetalMachinist) > 0,
					"the executed crystal preparation token survives to its planned burst");
				Game.Step(15.1f);
				Check(Game.Player.Statuses.GetValueOrDefault(StatusID.FullMetalMachinist) <= 0,
					"the prepared token still expires at its native 30-second lifetime");
			}
		}

		{
			var rotation = Reset(0x45A3);
			Game.Timeline.PlanWindows.Add(new(110, 130, false));
			Game.TtkOverride = 25;
			Game.Cooldown(BarrelStabilizerPvE).FullAt = 0;
			Game.Cooldown(WildfirePvE).FullAt = 120;
			rotation.NextGcd();
			Check(!(bool)typeof(MCH_SyncPlus).GetProperty("PrepareBarrelForBurst", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rotation)!,
				"early preparation refuses a target that cannot survive the remaining Wildfire cooldown plus payload");
			Check(rotation.NextAbility(noGcd)?.ID != (uint)BarrelStabilizerPvE,
				"an impending plan cannot consume Barrel for an impossible delayed Wildfire payload");
		}

		{
			var fatebreaker = new TimelineActor("fatebreaker", 0x459B, "P1", [[0, 100.1f]], [[0, .01f], [100.1f, 0]], [[0, 100, 100]], null);
			var shiva = new TimelineActor("shiva", 0x459F, "P2", [[104.8f, 2000]], [[0, 1]], [[0, 100, 100]], null);
			var fixture = new Timeline("P1 death while free Hypercharge remains", "MCH", 2000, [fatebreaker, shiva], [[0, 100, 100]], [], [], [], [], [], 0,
				[], [], [], "MCH", ["MCH", "PCT"], []);
			var rotation = (MCH_Plan)typeof(MchQueenDecisionCheck).GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static)!
				.Invoke(null, [0x459Bu, 100f, true, .01f, fixture])!;
			rotation.NextGcd(); // Confirm the preceding P1 actor before its death.
			Game.Step(.2f);
			Game.Player.Statuses[StatusID.Hypercharged] = 6f;
			Game.TtkOverride = 250;
			Check(!Game.Hostiles.Any(), "P1 transition regression has no attackable target");
			Check(rotation.NextAbility(noGcd)?.ID != (uint)HyperchargePvE,
				"an expiring free Hypercharge does not start its ten-second payload during P1 transition downtime");
			Game.Step(4.7f);
			Check(Game.Hostiles.Any() && Game.Player.Statuses.GetValueOrDefault(StatusID.Hypercharged) > 0,
				"the free Hypercharge token survives until P2 is attackable");
			rotation.NextGcd();
			Check(rotation.NextAbility(noGcd)?.ID == (uint)HyperchargePvE,
				"the preserved free Hypercharge can start once the next target is attackable");
		}

		// A known upcoming window does not authorize spending after the phase HP objective.
		foreach (var phase in new uint[] { 0x459F, 0x45A7 })
		{
			var rotation = Reset(phase, .10f);
			Game.Timeline.PlanWindows.Add(new(110, 130, false));
			Game.Cooldown(BarrelStabilizerPvE).FullAt = 0;
			Game.TtkOverride = 250;
			rotation.NextGcd();
			Check(rotation.NextAbility(noGcd)?.ID != (uint)BarrelStabilizerPvE,
				$"{phase:X}: a completed phase objective preserves Barrel");
		}

		// Actual on-cooldown selection must outrank optional Reassemble and capped-Heat Hypercharge.
		foreach (var phase in new uint[] { 0x45A7, 0x45A9, 0x45AB })
		{
			var rotation = Reset(phase);
			Game.TtkOverride = 250;
			Game.Cooldown(BarrelStabilizerPvE).FullAt = 0;
			Game.Cooldown(ReassemblePvE).FullAt = 0;
			Game.Cooldown(ChainSawPvE).FullAt = 0;
			Machinist.Gauge.Heat = 100;
			var next = rotation.NextGcd();
			Check(rotation.NextAbility(next ?? noGcd)?.ID == (uint)BarrelStabilizerPvE,
				$"{phase:X}: a ready Barrel takes priority over flexible weaves");
		}


		foreach (var phase in new uint[] { 0x45A9, 0x45AB })
		foreach (var mode in new[] { "live buffs", "no buffs", "late Wildfire", "dying" })
		{
			var rotation = Reset(phase);
			if (mode != "no buffs") Game.Timeline.PartyBuffs.Add(new("Starry Muse", "PCT", 1.05f, 99, 120));
			Game.Cooldown(WildfirePvE).FullAt = mode == "late Wildfire" ? 110 : 105;
			Game.TtkOverride = mode == "dying" ? 2 : 250;
			Game.Player.Statuses[StatusID.FullMetalMachinist] = 20;
			Game.Player.Statuses[StatusID.Hypercharged] = 20;
			Game.Step(.01f);
			rotation.NextGcd();
			var claimed = (bool)typeof(MCH_SyncPlus).GetProperty("PreparedBurstBarrelAwaitsWildfire", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rotation)!;
			Check(claimed == (mode == "live buffs"), $"P4 {phase:X} {mode}: only a safe imminent live-buff Wildfire claims Barrel tokens");
		}

		Console.WriteLine($"Early FRU Barrel: {checks} assertions; {failures.Count} failed.");
		if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
		return;

		void Check(bool result, string message)
		{
			checks++;
			if (!result) failures.Add(message);
		}
	}

	private static MCH_Plan Reset(uint phase, float hp = 1f)
		=> (MCH_Plan)typeof(MchQueenDecisionCheck).GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static)!
			.Invoke(null, [phase, 100f, true, hp, null])!;
}
