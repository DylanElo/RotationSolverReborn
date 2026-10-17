using System.Reflection;
using Dalamud.Game.ClientState.JobGauge.Enums;
using RotationSolver.Basic.Actions;
using RotationSolver.Basic.Data;
using RotationSolver.RebornRotations.Ranged;
using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

/// <summary>Replays the live CT recovery boundary without giving GeneralAbility a confirmation tick.</summary>
internal static class BardCtRecoveryCheck
{
	public static void Run(Timeline timeline)
	{
		var fixture = timeline with
		{
			Actors = [Actor("oracle", 0x45A7, 500, 610), Actor("pair", 0x45A9, 610, 775), Actor("pandora", 0x45AF, 853, 2000)],
			Duration = 2000, PartyBuffs = [], PlayerDebuffs = [], EnemyDebuffs = [], LoggedCasts = [], EnemyCasts = [],
			PlayerTarget = [], PlayerPos = [[0f, 100f, 100f]], PlanWindows = [],
		};
		var checks = 0;
		void Require(bool condition, string message)
		{
			if (!condition) throw new InvalidOperationException("BRD CT recovery: " + message);
			checks++;
		}
		BRD_Plan Reset(bool selected, bool confirmed, bool applied)
		{
			Game.Reset(fixture, new Bard(.2f, .2f), 0);
			Game.Step(758f);
			var rotation = new BRD_Plan();
			Set(rotation, "_ctFinaleSelected", selected);
			Set(rotation, "_ctFinaleConfirmed", confirmed);
			Set(rotation, "_lastTargetBaseId", (uint)0x45A9);
			Set(rotation, "_lastTargetHp", 1f);
			Bard.Gauge.Song = Song.WanderersMinuet;
			Bard.Gauge.SongEnd = 781f;
			Bard.Gauge.Repertoire = 1;
			Bard.Gauge.Coda = [Song.None, Song.None, Song.None];
			Game.Cooldown(TheWanderersMinuetPvE).FullAt = 856.79f;
			Game.Cooldown(MagesBalladPvE).FullAt = 780f;
			if (applied) Game.Player.Statuses[StatusID.RadiantFinale] = 5.955f;
			return rotation;
		}
		var pending = Reset(true, false, false);
		Require(Select(pending) == null, "a selected but unapplied Finale must not authorize Coda recovery");
		var ordinary = Reset(false, false, true);
		Require(Select(ordinary) == null, "an ordinary Finale tail must not authorize Coda recovery");

		var delayed = Reset(true, false, true);
		var first = Select(delayed);
		Require(first?.ID == (uint)PitchPerfectPvE, "Attack-first song selection must confirm the delayed CT status and drain PP");
		Require(Select(delayed)?.ID == (uint)PitchPerfectPvE, "selecting PP without its confirmed cast must not consume its drain allowance");
		Game.Execute(first!);
		Game.Step(.7f);
		Bard.Gauge.Repertoire = 1;
		Require(Select(delayed)?.ID == (uint)ArmysPaeonPvE, "new Repertoire after a confirmed PP must not postpone Army again");

		var expired = Reset(true, true, false);
		Bard.Gauge.Repertoire = 0;
		var army = Select(expired);
		Require(army?.ID == (uint)ArmysPaeonPvE, "confirmed CT recovery must survive Radiant Finale expiry");
		Game.Execute(army!);
		Game.Step(22.1f); // P4 is gone; Minuet remains 3.79s later than the known P5 return.
		Require(Game.DowntimeEndIn is > 0f and < 75f, "fixture exposes the known P5 return rather than unknown downtime");
		Require(Game.Cooldown(TheWanderersMinuetPvE).FullAt - Game.Now > Game.DowntimeEndIn + 2.5f,
			"fixture reproduces the live Minuet cooldown outside the generic grab allowance");
		var ballad = Select(expired);
		Require(ballad?.ID == (uint)MagesBalladPvE, "pending CT Ballad must recover despite the late Minuet and expired Finale");
		Game.Execute(ballad!);
		Game.Step(853.1f - Game.Now);
		Require(Select(expired) == null, "P5 return must not start Minuet before its cooldown finishes");
		Game.Step(856.81f - Game.Now);
		var minuet = Select(expired);
		Require(minuet?.ID == (uint)TheWanderersMinuetPvE, "Minuet starts at its actual cooldown after P5 returns");
		Game.Execute(minuet!);
		Require(Game.Cooldown(MagesBalladPvE).FullAt <= Game.Now + 45f,
			"recovered Ballad is genuinely ready after the full Minuet song");
		Game.Step(.7f);
		Require(BRD_Plan.RadiantFinalePvE.CanUse(out var finale, skipTTKCheck: true),
			"the first P5 Finale is executable after recovery");
		Game.Execute(finale);
		Require(Game.Casts[^1].Action == "RadiantFinalePvE (3 Codas)", "the first P5 Finale actually consumes all three recovered Codas");
		Require(Bard.Gauge.Coda.All(c => c == Song.None), "the three-Coda P5 Finale clears the Coda gauge");
		Console.WriteLine($"BRD CT recovery: {checks} stateful selector checks passed.");
	}

	private static TimelineActor Actor(string key, uint id, float start, float end) =>
		new(key, id, key, [[start, end]], [[0f, 1f]], [[0f, 100f, 100f]], null);
	private static void Set(object rotation, string name, object value) =>
		typeof(BRD_Reborn).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rotation, value);
	private static IAction? Select(BRD_Plan rotation)
	{
		object?[] args = [null];
		return (bool)typeof(BRD_Reborn).GetMethod("SongAbility", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rotation, args)!
			? (IAction?)args[0] : null;
	}
}
