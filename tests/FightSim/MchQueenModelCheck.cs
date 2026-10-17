using RotationSolver.Basic.Actions;
using RotationSolver.Basic.Data;
using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

internal static class MchQueenModelCheck
{
	public static void Run()
	{
		var checks = 0;
		foreach (var orderAt in new[] { -1f, 1f, 14.9f, 17.45f })
		{
			var actor = new TimelineActor("boss", 0x45AF, "Pandora", [[0, 100]], [[0, 1]], [[0, 100, 100]], null);
			var timeline = new Timeline("Queen model regression", "MCH", 100, [actor], [[0, 100, 100]], [], [], [], [], [], 0,
				[], [], [], "MCH", ["MCH"], [], false);
			var job = new Machinist(.2f, .2f);
			Game.Reset(timeline, job, 0);
			Game.Step(.02f);
			Machinist.Gauge.Battery = 100;
			Game.Execute(BaseAction.Create(AutomatonQueenPvE, (ref ActionSetting _) => { }));
			var summoned = Game.Now;
			if (orderAt >= 0)
			{
				while (Game.Now < summoned + orderAt) Game.Step(.02f);
				Game.Execute(BaseAction.Create(QueenOverdrivePvE, (ref ActionSetting _) => { }));
			}
			while (Game.Now < summoned + 25) Game.Step(.02f);
			foreach (var (name, potency) in new[] { ("Pile Bunker", 620f), ("Crowned Collider", 690f) })
			{
				var actual = Game.DamageByAction.GetValueOrDefault(name);
				var expected = potency * job.SelfMultiplier();
				if (Math.Abs(actual - expected) > .01)
					throw new InvalidOperationException($"Queen Overdrive at {orderAt}: {name} must land exactly once; {actual} != {expected}");
				checks++;
			}
		}
		Console.WriteLine($"Queen model: {checks} actual game-event assertions passed.");
	}
}
