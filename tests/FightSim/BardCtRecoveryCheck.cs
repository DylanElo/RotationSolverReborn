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
			Actors = [Actor("oracle", 0x45A7, 500, 610), Actor("pair", 0x45A9, 610, 782), Actor("pandora", 0x45AF, 853, 2000)],
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
		Require(Select(delayed) == null, "CT recovery must keep a running Minuet and its Repertoire for the pair's return");
		Game.Step(781f - Game.Now - 1f);
		Require(Select(delayed) == null, "one raw second of Minuet must survive both Coda recovery and ordinary song switching");
		Game.Step(.51f);
		Require(Select(delayed)?.ID == (uint)PitchPerfectPvE, "the last usable Repertoire must drain before recovering Codas");
		Game.Execute(Select(delayed)!);
		Game.Step(.7f);
		Require(Select(delayed)?.ID == (uint)MagesBalladPvE, "draining the final PP must release Coda recovery");

		var queued = Reset(true, true, false);
		Set(queued, "_ctRecoveryPpDrained", true);
		Game.Step(780f - Game.Now);
		Require(BRD_Plan.BurstShotPvE.CanUse(out var shot), "queued-GCD fixture must have an executable GCD");
		Game.Execute(shot);
		Game.Step(.7f);
		Bard.Gauge.SongEnd = Game.Now + 3.14f;
		Bard.Gauge.Repertoire = 0;
		Require(Game.GcdRemain > 1f && Select(queued) == null, "3.14 raw seconds must not be shortened by the remaining queued GCD");

		// Live 2026-10-18: Minuet cast at the CT Stun, the gauge still showed no song / a 0 timer, and recovery cast Army's Paeon 0.5s later;
		// the P5 Finale then had two Codas. A Minuet cast in the last seconds is a running Minuet whatever the gauge says.
		foreach (var lagged in new[] { Song.None, Song.WanderersMinuet })
		{
			var fresh = Reset(true, true, false);
			Bard.Gauge.Song = lagged;
			Bard.Gauge.SongEnd = Game.Now;
			Bard.Gauge.Repertoire = 0;
			Game.Cooldown(TheWanderersMinuetPvE).FullAt = Game.Now + 119.98f;
			Game.Cooldown(MagesBalladPvE).FullAt = Game.Now + 46f;
			Require(Select(fresh) == null, $"CT recovery must not replace a Minuet cast moments ago while the gauge reads {lagged}");
		}

		var expired = Reset(true, true, false);
		Bard.Gauge.Repertoire = 0;
		Require(Select(expired) == null, "confirmed CT recovery holds a running Minuet until its final weave");
		Game.Step(781f - Game.Now - 1f);
		Bard.Gauge.Repertoire = 0;
		Require(Select(expired) == null, "an expired Finale must not restore the old last-GCD cutoff");
		Game.Step(.51f);
		Bard.Gauge.Repertoire = 0;
		var ballad = Select(expired);
		Require(ballad?.ID == (uint)MagesBalladPvE, "pending CT Ballad must recover in Minuet's final raw half-second despite the expired Finale");
		Game.Execute(ballad!);
		Game.Step(.7f);
		var army = Select(expired);
		Require(army?.ID == (uint)ArmysPaeonPvE, "confirmed CT recovery must survive Radiant Finale expiry");
		Game.Execute(army!);
		Game.Step(782.1f - Game.Now);
		Require(Game.Cooldown(TheWanderersMinuetPvE).FullAt - Game.Now > Game.DowntimeEndIn + 2.5f,
			"fixture reproduces the live Minuet cooldown outside the generic grab allowance");
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
