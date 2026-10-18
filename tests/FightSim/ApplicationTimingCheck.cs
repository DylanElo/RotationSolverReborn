using RotationSolver.Basic.Actions;
using RotationSolver.Basic.Data;
using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

internal static class ApplicationTimingCheck
{
	public static void Run(Timeline timeline)
	{
		TimelineActor Actor(string key, float end) => new(key, 0x45A9, key,
			[[0f, end]], [[0f, 1f]], [[0f, 100f, 110f]], null);
		var fixture = timeline with
		{
			Actors = [Actor("main", 10), Actor("secondary", 2)],
			PartyBuffs = [], PlayerDebuffs = [], EnemyDebuffs = [], LoggedCasts = [], EnemyCasts = [],
			PlayerTarget = [], PlayerPos = [[0f, 100f, 100f]], PlanWindows = [],
		};
		var checks = 0;
		void Require(bool ok, string message)
		{
			if (!ok) throw new InvalidOperationException("Application timing: " + message);
			checks++;
		}
		void Reset(Timeline? input = null) => Game.Reset(input ?? fixture, new Bard(.2f, .2f), 0);
		void Cast(ActionID id)
		{
			var action = BaseAction.Create(id, (ref ActionSetting s) => s.CreateConfig = () => new ActionConfig { AoeCount = 1 });
			Require(action.CanUse(out var selected, skipTTKCheck: true), $"{id} is selectable");
			Game.Execute(selected);
		}
		Reset();
		Game.Step(1.05f);
		Cast(LadonsbitePvE);
		var first = Game.Casts.Count - 1;
		Require(Game.Casts[first].Damage == 0, "direct action has no damage before application");
		Game.Step(.7f);
		Game.Casts.Add((Game.Now, "later cast", null, 0));
		Game.Step(.7f);
		var applications = Game.DamageApplications.Where(a => a.CastIndex == first).ToArray();
		Require(applications.Count(a => a.Applied) == 1 && applications.Any(a => a.Target == "secondary" && !a.Applied),
			"recipient disappearance suppresses only that queued hit");
		Require(Game.Casts[first].Damage > 0 && Game.Casts[^1].Damage == 0, "damage is attributed to its originating cast");
		Require(applications.All(a => Math.Abs(a.Time - (1.05f + BardApplicationTiming.Delay("Ladonsbite", a.Target == "main" ? 0 : 1))) < .0001f),
			"large steps process each application at its own exact timestamp");
		Reset(fixture with { Actors = [Actor("main", 10)] });
		Game.Player.Statuses[StatusID.RagingStrikes] = .3f;
		Cast(StormbitePvE);
		Require(!Game.Hostiles[0].Statuses.ContainsKey(StatusID.Stormbite), "DoT status waits for application");
		Game.Step(1.4f);
		const double direct = 100 * 1.11 * 1.05 * 1.15;
		Require(Math.Abs(Game.Casts[0].Damage - direct) < .002, "direct hit keeps cast-time buffs after expiry");
		Require(Math.Abs(Game.Hostiles[0].Statuses[StatusID.Stormbite] - (45 - (1.4f - 1.289f))) < .002,
			"DoT duration starts on application");
		Game.Step(1.7f);
		Require(Math.Abs(Game.DamageByAction.GetValueOrDefault("Stormbite (DoT)") - 30 * 1.11 * 1.05 * 1.15) < .002,
			"DoT ticks retain the cast-time snapshot");
		Reset(fixture with { Actors = [Actor("main", .5f)] });
		Cast(StormbitePvE);
		Game.Step(1.4f);
		Require(!Game.Enemies[0].Chara.Statuses.ContainsKey(StatusID.Stormbite) && Game.Casts[0].Damage == 0,
			"disappearance suppresses both delayed damage and DoT application");
		Reset();
		Cast(LadonsbitePvE);
		Reset();
		Game.Step(1.5f);
		Require(!Game.DamageApplications.Any(a => a.Action == "Ladonsbite"), "reset clears pending applications");
		Reset(fixture with { Duration = .019f });
		Driver.Run(new RotationSolver.RebornRotations.Ranged.BRD_Plan(), 0, false);
		Require(Game.Now == Game.Timeline.Duration, "fight replay stops at its exact horizon without an overshooting step");
		Console.WriteLine($"Application timing: {checks} checks passed.");
	}
}
