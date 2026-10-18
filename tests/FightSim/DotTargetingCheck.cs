using System.Reflection;
using RotationSolver.Basic.Actions;
using RotationSolver.Basic.Data;
using RotationSolver.RebornRotations.Ranged;

namespace FightSim;

/// <summary>Exercise production DoT predicates through the simulator's automatic target resolver.</summary>
internal static class DotTargetingCheck
{
	public static void Run(Timeline timeline)
	{
		TimelineActor Actor(string key) => new(key, 0x45A9, key,
			[[0f, 2000f]], [[0f, 1f]], [[0f, 100f, 100f]], null);
		var fixture = timeline with
		{
			Actors = [Actor("current"), Actor("other")],
			PartyBuffs = [], PlayerDebuffs = [], EnemyDebuffs = [], LoggedCasts = [], EnemyCasts = [],
			PlayerTarget = [], PlayerPos = [[0f, 100f, 100f]], PlanWindows = [],
		};
		var checks = 0;
		void Require(bool ok, string message)
		{
			if (!ok) throw new InvalidOperationException("DoT targeting: " + message);
			checks++;
		}
		BRD_Plan Reset(Timeline? input = null)
		{
			Game.Reset(input ?? fixture, new Bard(.2f, .2f), 0);
			return new BRD_Plan();
		}
		void Dots(int index, float seconds)
		{
			Game.Hostiles[index].Statuses[StatusID.Stormbite] = seconds;
			Game.Hostiles[index].Statuses[StatusID.CausticBite] = seconds;
		}
		bool Select(BRD_Plan rotation, IBaseAction action, bool skip = false)
		{
			object?[] args = [action, null, skip, null];
			return (bool)typeof(BRD_Reborn).GetMethod("CanUseDoT", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rotation, args)!;
		}
		var jaws = BRD_Plan.IronJawsPvE;
		var storm = BRD_Plan.StormbitePvE;
		var rotation = Reset();
		Dots(0, 40); Dots(1, 4);
		var current = Game.Target;
		var predicate = jaws.Setting.CanTarget;
		Require(Select(rotation, jaws) && jaws.Target.Target == Game.Hostiles[1], "due other boss gets Iron Jaws while current boss is healthy");
		Require(Game.Target == current, "resolving another action target preserves the recorded hard target");
		Require(jaws.Setting.CanTarget == predicate, "production predicate is restored after resolution");
		rotation = Reset();
		Dots(0, 4); Dots(1, 4);
		Require(Select(rotation, jaws) && jaws.Target.Target == Game.Hostiles[0], "eligible current target retains preference");
		rotation = Reset();
		Dots(0, 40);
		Require(Select(rotation, storm) && storm.Target.Target == Game.Hostiles[1], "fresh DoT searches past the already-dotted current target");
		rotation = Reset();
		Game.Hostiles[0].Statuses[StatusID.Stormbite] = 4;
		Game.Hostiles[1].Statuses[StatusID.CausticBite] = 4;
		Require(!Select(rotation, jaws), "Iron Jaws requires both DoTs on the same target");
		Require(!Select(rotation, jaws, true), "skipping provided-status check still enforces native target predicate");
		rotation = Reset();
		Dots(0, 40); Dots(1, 40);
		Require(!Select(rotation, storm), "no fresh DoT candidate returns false");
		rotation = Reset();
		Game.TargetTtkOverride = target => target == Game.Hostiles[0] ? 4f : 60f;
		Require(Select(rotation, storm) && storm.Target.Target == Game.Hostiles[1], "dying first candidate does not hide a viable second boss");
		Console.WriteLine($"DoT targeting: {checks} production-predicate checks passed.");
	}
}
