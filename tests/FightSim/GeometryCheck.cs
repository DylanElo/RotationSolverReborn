using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

/// <summary>Independent rectangle/disk cases through the same geometry used for action resolution and damage.</summary>
internal static class GeometryCheck
{
	public static void Run(Timeline timeline)
	{
		var checks = 0;
		foreach (var id in new[] { ApexArrowPvE, BlastArrowPvE })
		foreach (var (x, y, hit, name) in new[]
		{
			(-3f, 0f, true, "hitbox overlapping the origin from behind"),
			(-6f, 0f, false, "hitbox wholly behind the origin"),
			(15f, 6f, true, "side intersects away from the selected target"),
			(15f, 8f, false, "outside the four-yalm line and hitbox"),
			(28f, 0f, true, "hitbox intersects the 25-yalm endpoint"),
			(31f, 0f, false, "beyond the endpoint and hitbox"),
			(-4f, 6f, false, "misses the rear corner despite expanded-box overlap"),
			(29f, 6f, false, "misses the far corner despite expanded-box overlap"),
		})
		{
			TimelineActor Actor(string key, float ax, float ay) => new(key, 0x459B, key,
				[[0f, 100f]], [[0f, 1f]], [[0f, ax, ay]], null);
			var fixture = timeline with
			{
				Actors = [Actor("main", 110, 100), Actor("secondary", 100 + x, 100 + y)],
				PartyBuffs = [], PlayerDebuffs = [], EnemyDebuffs = [], LoggedCasts = [], EnemyCasts = [],
				PlayerTarget = [], PlayerPos = [[0f, 100f, 100f]], PlanWindows = [],
			};
			var bard = new Bard(0.2f, 0.2f);
			Game.Reset(fixture, bard, 0);
			var spec = bard.Spec(id);
			if (spec.Shape != AoeShape.Line || spec.Radius != 25 ||
				Game.AffectedBy(spec, Game.Hostiles[0]).Contains(Game.Hostiles[1]) != hit)
				throw new InvalidOperationException($"Line geometry: {id}, {name}");
			checks++;
		}
		Console.WriteLine($"Line geometry: {checks} checks passed (Apex/Blast, 25y length, 4y width).");
	}
}
