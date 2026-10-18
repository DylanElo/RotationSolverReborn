using RotationSolver.Basic.Helpers;
using RotationSolver.RebornRotations.Ranged;
using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

/// <summary>Checks the shared CT availability policy and automatic Empyreal fallback.</summary>
internal static class TargetAvailabilityCheck
{
	public static void Run(Timeline timeline)
	{
		var checks = 0;
		void Require(bool condition, string name)
		{
			if (!condition) throw new InvalidOperationException("Target availability: " + name);
			checks++;
		}
		bool Allows(uint target, float downtime, float end = 40f, bool fru = true, bool manual = false)
			=> TargetAvailability.CanTarget(EmpyrealArrowPvE, target, fru, manual, downtime, end);
		foreach (var (target, boundary) in new[] { (0x45A9u, 2.125f), (0x45ABu, 1.025f) })
		{
			Require(!Allows(target, boundary - .01f), $"{target:X} rejects just inside the impact boundary");
			Require(!Allows(target, boundary), $"{target:X} rejects an impact exactly at disappearance");
			Require(Allows(target, boundary + .01f), $"{target:X} accepts just outside the impact boundary");
		}
		Require(!Allows(0x45A9, 1.4f) && Allows(0x45AB, 1.4f), "staggered pair availability distinguishes Frost and Oracle");
		foreach (var (start, end) in new[]
		{
			(float.MaxValue, float.MaxValue), (float.NaN, 40f), (1f, float.NaN),
			(float.PositiveInfinity, 40f), (1f, float.PositiveInfinity),
			(-.01f, 40f), (1f, 1f), (2f, 1f), (1f, 60.01f),
		})
			Require(Allows(0x45A9, start, end), $"unknown or non-CT forecast {start}/{end} preserves targeting");
		Require(!Allows(0x45A9, 0), "zero countdown remains a known unsafe CT boundary");
		Require(!Allows(0x45A9, 1, 60), "60-second CT upper bound is included");
		Require(Allows(0x45A9, 1, fru: false), "outside FRU is unchanged");
		Require(Allows(0x45A9, 1, manual: true), "manual target intent is unchanged");
		Require(Allows(0x45AF, 1), "other enemies are unchanged");
		Require(TargetAvailability.CanTarget(DrillPvE, 0x45A9, true, false, 1, 40), "unrelated job actions are unchanged");
		Require(TargetAvailability.CanTarget(BurstShotPvE, 0x45A9, true, false, 1, 40), "unrelated BRD actions are unchanged");

		TimelineActor Actor(string key, uint id, float hp) => new(key, id, key,
			[[0f, 100f]], [[0f, hp]], [[0f, 100f, 110f]], null);
		var fixture = timeline with
		{
			Duration = 100,
			Actors = [Actor("frost", 0x45A9, .2003f), Actor("oracle", 0x45AB, .1805f)],
			PartyBuffs = [], PlayerDebuffs = [], EnemyDebuffs = [], LoggedCasts = [], EnemyCasts = [],
			PlayerTarget = [], PlayerPos = [[0f, 100f, 100f]], PlanWindows = [],
		};
		void Reset(float start = 1.4f, float end = 40f)
		{
			Game.Reset(fixture, new Bard(.2f, .2f), 0);
			Game.Step(10f);
			Game.CurrentTargetOverride = Game.Hostiles[0];
			Game.ForecastOverride = (start, end);
		}
		var ea = BRD_Plan.EmpyrealArrowPvE;
		Reset();
		var hardTarget = Game.CurrentTargetOverride;
		Require(ea.CanUse(out var selected) && ea.Target.Target == Game.Hostiles[1], "automatic EA falls back to surviving Oracle");
		Require(Game.CurrentTargetOverride == hardTarget, "resolving EA preserves the hard target");
		Game.Execute(selected!);
		Require(Game.Casts[^1].Target == "oracle", "executed EA uses the resolved fallback target");
		Game.Step(1.2f);
		Require(Game.Casts[^1].Damage > 0, "fallback EA applies damage");
		Reset(1f);
		Require(!ea.CanUse(out _), "both pair targets unsafe rejects EA without fallback to Frost");
		Reset();
		Game.TargetTtkOverride = _ => 1000f;
		Require(ea.CanUse(out _) && ea.Target.Target == Game.Hostiles[1], "high HP and long TTK do not hide mechanic disappearance");
		Reset(float.MaxValue, float.MaxValue);
		Require(ea.CanUse(out _) && ea.Target.Target == Game.Hostiles[0], "unknown forecast preserves the current target");
		Reset();
		Game.ManualTargeting = true;
		Require(ea.CanUse(out _) && ea.Target.Target == Game.Hostiles[0], "manual EA preserves Frost");
		Reset();
		Game.IsInFRU = false;
		Require(ea.CanUse(out _) && ea.Target.Target == Game.Hostiles[0], "outside FRU current targeting is unchanged");
		Reset(1.4f, 80f);
		Require(ea.CanUse(out _) && ea.Target.Target == Game.Hostiles[0], "long downtime outside CT preserves current targeting");
		Reset();
		Require(BRD_Plan.BurstShotPvE.CanUse(out _) && BRD_Plan.BurstShotPvE.Target.Target == Game.Hostiles[0], "unrelated action keeps the recorded target");
		Console.WriteLine($"Target availability: {checks} shared-policy and action-resolution checks passed.");
	}
}
