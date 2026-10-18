using Dalamud.Game.ClientState.JobGauge.Enums;
using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

internal static class ReturnPositionCheck
{
	public static void Run(Timeline timeline)
	{
		var checks = 0;
		void Require(bool ok, string message)
		{
			if (!ok) throw new InvalidOperationException("Return positions: " + message);
			checks++;
		}
		TimelineActor Actor(string key, List<float[]> pos) => new(key, 0x45A9, key,
			[[10f, 20f]], [[0f, 1f], [10.177f, .5f]], pos, null);
		var fixture = timeline with
		{
			Actors = [Actor("main", [[0f, 120f, 100f], [10.177f, 96.45f, 99.99f], [11f, 130f, 100f]]),
				Actor("secondary", [[0f, 100f, 105f], [10.177f, 103.53f, 99.99f]])],
			PartyBuffs = [], PlayerDebuffs = [], EnemyDebuffs = [], LoggedCasts = [], EnemyCasts = [],
			PlayerTarget = [], PlayerPos = [[0f, 100f, 90f], [10.177f, 100f, 100f]], PlanWindows = [],
		};
		Bard Reset(Timeline? input = null)
		{
			var bard = new Bard(.2f, .2f);
			Game.Reset(input ?? fixture, bard, 0);
			return bard;
		}
		var bard = Reset();
		Game.Step(9.99f);
		Require(Game.Enemies[0].Position.X == 120f, "future return pose is not borrowed before targetability");
		Game.Step(.04f);
		Require(Game.Enemies[0].Position.X == 96.45f && Game.Enemies[1].Position.X == 103.53f,
			"both returning bosses use their bounded entry pose");
		Require(Game.Enemies[0].Chara.GetHealthRatio() == 1f && Enemy.Sample(Game.Timeline.PlayerPos, 2, 0f) == 90f,
			"HP and player sampling remain chronological");
		var affected = Game.AffectedBy(bard.Spec(RadiantEncorePvE), Game.Hostiles[0]);
		Require(affected.Length == 2, "Encore reaches both overlapping return bosses");
		Bard.Gauge.Coda = [Song.WanderersMinuet, Song.MagesBallad, Song.None];
		Game.Casts.Add((Game.Now, "RadiantFinalePvE", null, 0));
		bard.Execute(bard.Spec(RadiantFinalePvE), Game.Player, []);
		Game.Casts.Add((Game.Now, "RadiantEncorePvE", "main", 0));
		bard.Execute(bard.Spec(RadiantEncorePvE), Game.Hostiles[0], affected);
		const double expected = 800 * 1.5 * 1.11 * 1.05 * 1.04;
		Require(Math.Abs(Game.Casts[^1].Damage - expected) < .002, "two-Coda Encore emits primary plus half-potency secondary");
		Game.Step(10.177f - Game.Now);
		Require(Game.Enemies[0].Position.X == 96.45f, "exact entry timestamp resolves normally");
		Game.Step(.1f);
		Require(Game.Enemies[0].Position.X == 96.45f, "existing in-span sample wins over later movement");
		Reset(fixture with { Actors = [Actor("late", [[0f, 120f, 100f], [10.6f, 96f, 100f]])] });
		Game.Step(10.1f);
		Require(Game.Enemies[0].Position.X == 120f, "entry samples later than 500ms are not borrowed");
		Reset(fixture with { Actors = [Actor("next-span", [[0f, 120f, 100f], [11f, 96f, 100f]]) with
			{ Spans = [[10f, 10.2f], [11f, 20f]] }] });
		Game.Step(10.1f);
		Require(Game.Enemies[0].Position.X == 120f, "a different targetability span cannot supply the return pose");
		Reset(timeline);
		var pair = Game.Enemies.Where(e => e.Actor.BaseId is 0x45A9 or 0x45AB).ToArray();
		var returnAt = pair.Max(e => e.Actor.Spans[1][0]);
		Game.Step(returnAt + .034f);
		Require(Game.AffectedBy(Game.Job.Spec(RadiantEncorePvE), pair[0].Chara).Length == 2,
			"reference CT Encore sees two targets before the first logged return sample");
		Console.WriteLine($"Return positions: {checks} checks passed.");
	}
}
