using Dalamud.Game.ClientState.JobGauge.Enums;
using RotationSolver.Basic.Data;
using static RotationSolver.Basic.Data.ActionID;

namespace FightSim;

/// <summary>Checks the damage actually emitted by Bard.Execute, independent of rotation selection.</summary>
internal static class BardDamageCheck
{
	public static void Run(Timeline timeline)
	{
		var fixture = timeline with
		{
			Actors = [Actor("main"), Actor("secondary")],
			PartyBuffs = [], PlayerDebuffs = [], EnemyDebuffs = [], LoggedCasts = [], EnemyCasts = [],
			PlayerTarget = [], PlayerPos = [[0f, 100f, 100f]], PlanWindows = [],
		};
		var checks = 0;
		void Require(bool condition, string name)
		{
			if (!condition) throw new InvalidOperationException($"BRD damage model: {name}");
			checks++;
		}
		void Equal(double actual, double expected, string name) =>
			Require(Math.Abs(actual - expected) < 0.002, $"{name}: expected {expected:F5}, got {actual:F5}");
		Bard Reset()
		{
			var bard = new Bard(0.2f, 0.2f);
			Game.Reset(fixture, bard, 0);
			return bard;
		}
		void Cast(Bard bard, ActionID id, bool aoe = false)
		{
			Game.Casts.Add((Game.Now, id.ToString(), "main", 0));
			bard.Execute(bard.Spec(id), Game.Hostiles[0], aoe ? Game.Hostiles.ToArray() : [Game.Hostiles[0]]);
		}
		// Independent geometry examples: the old 90 degree approximation rejected the first side target.
		foreach (var (mainX, mainY, sideX, sideY, count, name) in new[]
		{
			(100f, 110f, 108f, 100f, 2, "main-hitbox widening includes the side target"),
			(100f, 110f, 120f, 100f, 1, "radial range rejects a distant side target"),
			(100f, 110f, 100f, 90f, 1, "cone rejects a target behind its aim"),
			(100f, 100f, 110f, 100f, 2, "coincident main target uses the radial fallback"),
		})
		{
			var geometry = fixture with { Actors = [Actor("main") with { Pos = [[0f, mainX, mainY]] },
				Actor("secondary") with { Pos = [[0f, sideX, sideY]] }] };
			var bard = new Bard(0.2f, 0.2f);
			Game.Reset(geometry, bard, 0);
			Require(Game.AffectedBy(bard.Spec(LadonsbitePvE), Game.Hostiles[0]).Length == count, name);
		}
		const double baseExpectation = (1 + 0.2 * 0.55) * (1 + 0.2 * 0.25);
		foreach (var soul in new byte[] { 80, 100 })
		{
			var bard = Reset();
			Bard.Gauge.SoulVoice = soul;
			Cast(bard, ApexArrowPvE);
			Equal(Game.Casts[^1].Damage, 7 * soul * baseExpectation, $"Apex has 700 potency at 100 Soul Voice ({soul})");
		}
		{
			var bard = Reset();
			Cast(bard, ResonantArrowPvE, true);
			Equal(Game.Casts[^1].Damage, 640 * 1.5 * baseExpectation, "Resonant has 640 potency and 50% falloff");
		}
		{
			var buffs = fixture with
			{
				PartyBuffs = [new("Battle Litany", "DRG", 1f, 0, 1)],
				EnemyDebuffs = [new("main", "Chain Stratagem", "SCH", 1f, 0, 1), new("main", "Mug", "NIN", 1.05f, 0, 1)],
			};
			var bard = new Bard(0.2f, 0.2f);
			Game.Reset(buffs, bard, 0);
			var chainLitany = (1 + 0.4 * 0.55) * 1.05 * 1.05;
			Equal(Game.Multiplier(Game.Hostiles[0]), chainLitany, "Litany and target Chain add crit chance; Mug multiplies target damage");
			Equal(Game.Multiplier(Game.Hostiles[1]), (1 + 0.3 * 0.55) * 1.05, "Chain and Mug do not affect the other target");
			Cast(bard, CausticBitePvE);
			Equal(Game.Casts[^1].Damage, 150 * chainLitany, "direct hit uses target buffs");
			Game.Step(3);
			Equal(Game.Multiplier(Game.Hostiles[0]), baseExpectation, "expired Litany, Chain and Mug stop affecting new hits");
			Equal(Game.DamageByAction.GetValueOrDefault("Caustic Bite (DoT)"), 24 * chainLitany,
				"DoT retains target Chain and Mug and party Litany from application after their expiry");
		}
		{
			var logged = fixture with
			{
				PartyBuffs = [new("Battle Litany", "DRG", 1f, 1, 3)],
				LoggedCasts = System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement[]>>("[[0,\"Burst Shot\"],[2,\"Burst Shot\"]]")!,
				LoggedHits = System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement[]>>("[[0,\"Burst Shot\",220,false,false,false],[2,\"Burst Shot\",220,false,false,false]]")!,
			};
			var normalized = new Bard(0.2f, 0.2f).LogPotency(logged);
			Equal(normalized[0].P, 220 * baseExpectation, "logged ordinary hit receives base expected crit and DH");
			Equal(normalized[1].P, 220 * (1 + 0.3 * 0.55) * 1.05, "logged Litany hit receives its expected crit bonus");
		}
		foreach (var (song, expectation) in new[]
		{
			(TheWanderersMinuetPvE, (1 + 0.22 * 0.55) * (1 + 0.2 * 0.25)),
			(MagesBalladPvE, baseExpectation * 1.01),
			(ArmysPaeonPvE, (1 + 0.2 * 0.55) * (1 + 0.23 * 0.25)),
		})
		{
			var bard = Reset();
			Cast(bard, song);
			Cast(bard, BurstShotPvE);
			Equal(Game.Casts[^1].Damage, 220 * expectation, $"{song} contributes its party bonus to the Bard's hit");
			Bard.Gauge.SongEnd = 0;
			bard.Advance(0);
			Cast(bard, BurstShotPvE);
			Equal(Game.Casts[^1].Damage, 220 * baseExpectation, $"{song} bonus expires with the song");
		}

		for (var codas = 1; codas <= 3; codas++)
		{
			var bard = Reset();
			Bard.Gauge.Coda = new[] { Song.WanderersMinuet, Song.MagesBallad, Song.ArmysPaeon }
				.Select((song, i) => i < codas ? song : Song.None).ToArray();
			Cast(bard, RadiantFinalePvE);
			Require(Bard.Gauge.Coda.All(c => c == Song.None), $"{codas}-Coda Finale consumes its Codas");
			Cast(bard, RadiantEncorePvE, true);
			var potency = codas switch { 1 => 700, 2 => 800, _ => 1100 };
			Equal(Game.Casts[^1].Damage, potency * 1.5 * baseExpectation * (1 + 0.02 * codas),
				$"{codas}-Coda Encore potency, Finale multiplier and secondary falloff");
			Require(!Game.Player.Statuses.ContainsKey(StatusID.RadiantEncoreReady), "Encore consumes its ready status");
		}

		foreach (var barrage in new[] { false, true })
		{
			var bard = Reset();
			Game.Player.Statuses[StatusID.HawksEye_3861] = 30f;
			if (barrage) Cast(bard, BarragePvE);
			Cast(bard, ShadowbitePvE, true);
			Equal(Game.Casts[^1].Damage, (barrage ? 300 : 200) * 2 * baseExpectation,
				$"Shadowbite {(barrage ? "with" : "without")} Barrage hits both targets at full potency");
			Require(!Game.Player.Statuses.ContainsKey(StatusID.HawksEye_3861) && !Game.Player.Statuses.ContainsKey(StatusID.Barrage),
				"Shadowbite consumes Hawk's Eye and Barrage");
		}

		for (byte stacks = 1; stacks <= 3; stacks++)
		{
			var bard = Reset();
			Cast(bard, TheWanderersMinuetPvE);
			Bard.Gauge.Repertoire = stacks;
			Cast(bard, PitchPerfectPvE, true);
			var potency = stacks switch { 1 => 100, 2 => 220, _ => 360 };
			Equal(Game.Casts[^1].Damage, potency * 1.5 * (1 + 0.22 * 0.55) * (1 + 0.2 * 0.25),
				$"{stacks}-stack Pitch Perfect has 50% secondary falloff");
			Require(Bard.Gauge.Repertoire == 0, "Pitch Perfect consumes Repertoire");
		}
		var p4Start = timeline.Actors.Where(a => a.BaseId is 0x45A9 or 0x45AB)
			.Select(a => a.Spans[0][0]).DefaultIfEmpty(float.MaxValue).Min();
		var p5Start = timeline.Actors.Where(a => a.BaseId == 0x45AF)
			.Select(a => a.Spans[0][0]).DefaultIfEmpty(timeline.Duration).Min();
		foreach (var cast in timeline.LoggedCasts.Where(c => c[1].GetString() == "Ladonsbite"
			&& c[0].GetSingle() >= p4Start && c[0].GetSingle() < p5Start))
		{
			var at = cast[0].GetSingle();
			var loggedTargets = timeline.LoggedHits.Count(h => h[1].GetString() == "Ladonsbite" && !h[5].GetBoolean()
				&& h[0].GetSingle() >= at && h[0].GetSingle() < at + 2.4f);
			var bard = new Bard(0.2f, 0.2f);
			Game.Reset(timeline, bard, 0);
			Game.Step(at);
			Require(Game.Target != null && Game.AffectedBy(bard.Spec(LadonsbitePvE), Game.Target).Length == loggedTargets,
				$"logged P4 Ladonsbite at {at:F3}s reaches its {loggedTargets} observed targets");
		}
		Console.WriteLine($"BRD damage model: {checks} checks passed.");
	}

	private static TimelineActor Actor(string key) => new(key, 0x45A9, key,
		[[0f, 2000f]], [[0f, 1f]], [[0f, 100f, 100f]], null);
}
