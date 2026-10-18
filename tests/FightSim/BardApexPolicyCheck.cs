using System.Reflection;
using Dalamud.Game.ClientState.JobGauge.Enums;
using RotationSolver.Basic.Data;
using RotationSolver.RebornRotations.Ranged;
using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

/// <summary>Exercises the production Apex selector and its capped-gauge exceptions.</summary>
internal static class BardApexPolicyCheck
{
	public static void Run(Timeline timeline)
	{
		var checks = 0;
		PolicyRotation Reset(byte soul = 100, bool hold = false, float plannedIn = 0f, bool final = false)
		{
			var fixture = timeline with
			{
				Duration = 1000,
				Actors = [new("boss", final ? 0x45AFu : 0x45A9u, "boss", [[0f, 1000f]], [[0f, 1f]], [[0f, 100f, 110f]], null)],
				PartyBuffs = [], PlayerDebuffs = [], EnemyDebuffs = [], LoggedCasts = [], EnemyCasts = [],
				PlayerTarget = [], PlayerPos = [[0f, 100f, 100f]], PlanWindows = [],
			};
			Game.Reset(fixture, new Bard(.2f, .2f), 0);
			Game.Step(100f);
			Game.TtkOverride = final ? 3f : 500f;
			Bard.Gauge.Song = Song.WanderersMinuet;
			Bard.Gauge.SongEnd = Game.Now + 30f;
			Bard.Gauge.SoulVoice = soul;
			foreach (var target in Game.Hostiles)
			{
				target.Statuses[StatusID.Stormbite] = 45f;
				target.Statuses[StatusID.CausticBite] = 45f;
			}
			Game.Cooldown(BattleVoicePvE).FullAt = Game.Now + 13f;
			Game.Cooldown(RagingStrikesPvE).FullAt = Game.Now + 11.58f;
			return new PolicyRotation(hold, plannedIn);
		}
		void Require(PolicyRotation rotation, bool expected, string name)
		{
			if (!PolicyRotation.ApexArrowPvE.CanUse(out _))
				throw new InvalidOperationException("BRD Apex policy: fixture cannot use Apex: " + name);
			object?[] args = [null];
			var actual = (bool)typeof(BRD_Reborn).GetMethod("CanUseApexArrow", BindingFlags.Instance | BindingFlags.NonPublic)!
				.Invoke(rotation, args)!;
			if (actual != expected) throw new InvalidOperationException($"BRD Apex policy: {name}: expected {expected}, got {actual}");
			checks++;
		}
		Require(Reset(), false, "capped Minuet holds for Battle Voice in 13s despite Raging Strikes outside 10s");
		Require(Reset(80), false, "partial gauge also waits for the nearby burst");
		var distant = Reset();
		Game.Cooldown(BattleVoicePvE).FullAt = Game.Now + 30f;
		Require(distant, true, "capped Minuet still spends when Battle Voice is distant");
		var boundary = Reset();
		Game.Cooldown(BattleVoicePvE).FullAt = Game.Now + 25f;
		Require(boundary, false, "near-burst hold includes the 25s boundary");
		var outside = Reset();
		Game.Cooldown(BattleVoicePvE).FullAt = Game.Now + 25.1f;
		Require(outside, true, "capped Minuet spends outside the 25s boundary");
		var manualBurst = Reset();
		Game.AutoBurst = false;
		Require(manualBurst, true, "AutoBurst off does not wait indefinitely");
		Game.ForecastOverride = (.55f, 80f);
		Game.TargetTtkOverride = _ => .55f;
		Require(manualBurst, true, "AutoBurst off retains priority at an imminent downtime and target-lifetime boundary");
		Require(Reset(100, true, 68f), true, "long planned hold permits capped-gauge spending");
		Require(Reset(80, true, 68f), false, "long planned hold does not dump a partial gauge");
		var payloadWindow = Game.GcdTotal * 2f + .5f;
		foreach (var (downtime, expected) in new[]
		{
			(0f, false), (.55f, false), (payloadWindow - .01f, false), (payloadWindow, false),
			(payloadWindow + .01f, true), (float.MaxValue, true), (float.NaN, true), (-1f, true),
		})
		{
			var late = Reset(100, true, 999f);
			Bard.Gauge.Song = Song.MagesBallad;
			Game.Cooldown(BattleVoicePvE).FullAt = Game.Now + 110f;
			Game.ForecastOverride = (downtime, 80f);
			Require(late, expected, $"long-hold Ballad Apex with downtime in {downtime}s cannot bypass the boundary guard");
		}
		foreach (var (targetLife, expected) in new[]
		{
			(0f, false), (.55f, false), (payloadWindow, false), (payloadWindow + .01f, true),
			(float.NaN, true), (-1f, true),
		})
		{
			var killGated = Reset(100, true, 999f);
			Bard.Gauge.Song = Song.MagesBallad;
			Game.Cooldown(BattleVoicePvE).FullAt = Game.Now + 110f;
			Game.ForecastOverride = (float.MaxValue, float.MaxValue);
			Game.TargetTtkOverride = _ => targetLife;
			Game.AverageTtkOverride = 500f;
			Require(killGated, expected, $"long-hold Apex respects resolved target lifetime {targetLife}s with no downtime forecast");
		}
		var buffed = Reset();
		Game.Player.Statuses[StatusID.RagingStrikes] = 15f;
		Game.Player.Statuses[StatusID.BattleVoice] = 15f;
		Game.Cooldown(BattleVoicePvE).FullAt = Game.Now + 110f;
		Require(buffed, true, "active burst spends Apex normally");
		Require(Reset(final: true), true, "final-kill dump overrides the approaching burst hold");
		var finalBoundary = Reset(100, true, 999f, final: true);
		Game.ForecastOverride = (.55f, 80f);
		Require(finalBoundary, true, "final-kill dumping retains priority over the long-hold boundary guard");
		Console.WriteLine($"BRD Apex policy: {checks} stateful selector checks passed.");
	}

	private sealed class PolicyRotation(bool hold, float plannedIn) : BRD_Reborn
	{
		private protected override bool HoldBurst => hold;
		private protected override float PlannedBurstIn => plannedIn;
	}
}
