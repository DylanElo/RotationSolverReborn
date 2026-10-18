using System.Text.Json;

namespace FightSim;

internal static class ReferenceCalibrationCheck
{
	public static void Run(Timeline timeline)
	{
		var checks = 0;
		const double expected = (1 + 0.2 * 0.55) * (1 + 0.2 * 0.25);
		TimelineActor Actor(string key, float start, float end, float taken = 1) =>
			new(key, 1, key, [[start, end]], [], [], null, taken);
		List<JsonElement[]> Rows(string json) => JsonSerializer.Deserialize<List<JsonElement[]>>(json)!;
		var fixture = timeline with
		{
			Actors = [Actor("normal", 0, 2), Actor("reduced", 2, 4, 0.5f)],
			PartyBuffs = [], PlayerDebuffs = [], LoggedCasts = [], PlayerTarget = [],
			LoggedHits = Rows("[[1,\"Burst Shot\",220,false,false,false],[3,\"Burst Shot\",110,false,false,false]]"),
		};
		void Equal(double actual, double value, string name)
		{
			if (Math.Abs(actual - value) > 0.002)
				throw new InvalidOperationException($"Reference calibration: {name}: expected {value:F5}, got {actual:F5}");
			checks++;
		}
		List<(float T, string Name, double P)> Score(Timeline value) => new Bard(0.2f, 0.2f).LogPotency(value);
		var scored = Score(fixture);
		Equal(scored[0].P, 220 * expected, "reduced target does not depress the calibration factor");
		Equal(scored[1].P, 110 * expected, "normalized damage retains target mitigation");
		var overlap = fixture with
		{
			Actors = [Actor("normal", 0, 4), Actor("reduced", 2, 4, 0.5f)],
			PlayerTarget = Rows("[[0,\"normal\"],[2,\"reduced\"]]"),
		};
		Equal(Score(overlap)[0].P, 220 * expected, "recorded target resolves overlapping mitigation rates");
		var ambiguous = overlap with { PlayerTarget = [], LoggedHits = Rows("[[1,\"Burst Shot\",220,false,false,false],[3,\"Burst Shot\",999,false,false,false]]") };
		Equal(Score(ambiguous)[0].P, 220 * expected, "unknown target with mixed mitigation is excluded from calibration");
		var absent = fixture with { Actors = [Actor("normal", 0, 2)] };
		Equal(Score(absent)[0].P, 220 * expected, "hit without target evidence is excluded from calibration");
		var common = overlap with { Actors = [Actor("normal", 0, 4), Actor("other", 2, 4)], PlayerTarget = [] };
		common = common with { LoggedHits = Rows("[[1,\"Burst Shot\",220,false,false,false],[3,\"Burst Shot\",220,false,false,false]]") };
		Equal(Score(common)[0].P, 220 * expected, "shared mitigation needs no target identity");
		Timeline FinaleFixture(string casts, double multiplier) => fixture with
		{
			Actors = [Actor("normal", 0, 120)],
			LoggedCasts = Rows(casts),
			LoggedHits = Rows($"[[59,\"Burst Shot\",220,false,false,false],[61,\"Burst Shot\",{220 * multiplier},false,false,false]]"),
		};
		const string minuet = "[0,\"The Wanderer's Minuet\"]";
		const string ballad = "[1,\"Mage's Ballad\"]";
		const string paeon = "[2,\"Army's Paeon\"]";
		var songs = new[] { minuet, ballad, paeon };
		for (var count = 1; count <= 3; count++)
		{
			var value = FinaleFixture($"[{string.Join(",", songs.Take(count))},[60,\"Radiant Finale\"]]", 1 + 0.02 * count);
			var result = Score(value);
			Equal(result[0].P, 220 * expected, $"{count}-Coda Finale does not distort calibration");
			Equal(result[1].P, 220 * (1 + 0.02 * count) * expected, $"{count}-Coda Finale retains its damage multiplier");
		}
		var consumed = FinaleFixture($"[{minuet},{ballad},[10,\"Radiant Finale\"],[11,\"Army's Paeon\"],[60,\"Radiant Finale\"]]", 1.02);
		Equal(Score(consumed)[0].P, 220 * expected, "Finale consumes accumulated Codas");
		var repeated = FinaleFixture($"[{minuet},[1,\"The Wanderer's Minuet\"],[60,\"Radiant Finale\"]]", 1.02);
		Equal(Score(repeated)[0].P, 220 * expected, "repeated song does not add another distinct Coda");
		var died = FinaleFixture($"[{minuet},{ballad},[11,\"Army's Paeon\"],[60,\"Radiant Finale\"]]", 1.02)
			with { PlayerDebuffs = [new("Dead", 10, 10.5f)] };
		Equal(Score(died)[0].P, 220 * expected, "death clears Codas before subsequent song casts");
		var lostFinale = FinaleFixture($"[{minuet},[50,\"Radiant Finale\"]]", 1)
			with { PlayerDebuffs = [new("Dead", 55, 56)] };
		Equal(Score(lostFinale)[0].P, 220 * expected, "death removes an active Finale damage multiplier");
		Console.WriteLine($"Reference calibration: {checks} checks passed.");
	}
}
