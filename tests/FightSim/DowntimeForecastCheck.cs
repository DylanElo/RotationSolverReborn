namespace FightSim;

internal static class DowntimeForecastCheck
{
	public static void Run()
	{
		var checks = 0;
		void Expect(DowntimeForecast forecast, float now, float start, float end, string name)
		{
			var actual = forecast.At(now);
			bool Equal(float a, float b) => a == b || Math.Abs(a - b) < 0.002f;
			if (!Equal(actual.Start, start) || !Equal(actual.End, end))
				throw new InvalidOperationException($"{name} at {now}: expected ({start}, {end}), got {actual}");
			++checks;
		}
		const float unknown = float.MaxValue;
		TimelineActor Actor(uint id, params float[][] spans) => new(id.ToString(), id, "fixture", [.. spans], [], [], null);
		var timeline = new Timeline("synthetic", "MCH", 520,
			[Actor(0x459B, [0, 10], [20, 30]), Actor(0x459F, [34, 40], [50, 60], [70, 80]),
			 Actor(0x45A3, [100, 110]), Actor(0x45A5, [103, 120]), Actor(0x45A7, [150, 210]),
			 Actor(0x45A9, [220, 260], [300, 330]), Actor(0x45AB, [240, 262], [300, 329]), Actor(0x45AF, [407, 500])],
			[], [], [], [new("Stun", 180, 184), new("Stun", 280, 290)], [], [], 0,
			[new("17831", 40286, 183.9f, 3)], [], []);
		var forecast = new DowntimeForecast(timeline);
		Expect(forecast, -1, unknown, unknown, "inactive");
		Expect(forecast, 5, 5, 15, "P1 known mechanic");
		Expect(forecast, 10, unknown, 10, "elapsed start is not zero");
		Expect(forecast, 29, unknown, unknown, "P1 kill and P2 return hidden");
		Expect(forecast, 30, 10, 4, "P2 activated at kill");
		Expect(forecast, 45, 15, 5, "start and end searched independently");
		Expect(forecast, 90, unknown, 10, "intermission crystals return");
		Expect(forecast, 115, unknown, unknown, "Ice Veil kill hidden");
		Expect(forecast, 120, 60, 30, "P34 activated");
		Expect(forecast, 179, 1, 4.9f, "UR flagged while targetable");
		Expect(forecast, 181, 29, 2.9f, "UR ends at Shell Crusher start");
		Expect(forecast, 215, 47, 5, "P3 to P4 is same phase");
		Expect(forecast, 261, 1, 39, "CT starts when second boss disappears");
		Expect(forecast, 281, unknown, 19, "CT stun adds no extra flags");
		Expect(forecast, 329.5f, unknown, unknown, "P5 hidden before P4 kill");
		Expect(forecast, 330, unknown, 77, "P5 visible after P4 kill");
		Expect(forecast, 408, unknown, unknown, "P5 has no future flags");
		Expect(forecast, 510, unknown, unknown, "terminal wipe is not a flag");
		void Plan(float now, float at, bool expected, string name)
		{
			if (forecast.IsInCurrentPhase(now, at) != expected) throw new InvalidOperationException(name);
			++checks;
		}
		Plan(29, 36, false, "P2 plan hidden before P1 kill");
		Plan(30, 36, true, "P2 plan visible at phase entry");
		Plan(200, 310, true, "P3 and P4 share a plan branch");
		Plan(329, 410, false, "P5 plan hidden before P4 kill");
		Plan(330, 410, true, "P5 plan visible before boss returns");

		foreach (var pull in new[] { 5, 27 })
		{
			var file = $"fru-mch-jJXkAtKaFQPyH4Wn-{pull}.json";
			var directory = new DirectoryInfo(AppContext.BaseDirectory);
			string? path = null;
			while (directory != null && path == null)
			{
				var candidate = Path.Combine(directory.FullName, "timelines", file);
				if (File.Exists(candidate)) path = candidate;
				directory = directory.Parent;
			}
			if (path == null) throw new FileNotFoundException($"Missing required MCH fixture {file}");
			var replay = Timeline.Load(path);
			var actual = new DowntimeForecast(replay);
			var usurper = replay.Actors.Single(a => a.BaseId == 0x45A9);
			var oracle = replay.Actors.Single(a => a.BaseId == 0x45AB);
			var p3 = replay.Actors.Single(a => a.BaseId == 0x45A7);
			var p5 = replay.Actors.Single(a => a.BaseId == 0x45AF);
			var ctStart = oracle.Spans[0][1];
			var ctEnd = usurper.Spans[1][0];
			var kill = Math.Max(usurper.Spans[^1][1], oracle.Spans[^1][1]);
			Expect(actual, ctStart - 1, 1, ctEnd - ctStart + 1, $"#{pull} CT forecast");
			Expect(actual, ctStart + 1, unknown, ctEnd - ctStart - 1, $"#{pull} during CT");
			Expect(actual, ctEnd + 1, unknown, unknown, $"#{pull} P4 kill hidden");
			Expect(actual, kill + 1, unknown, p5.Spans[0][0] - kill - 1, $"#{pull} P5 return revealed");
			Expect(actual, replay.Duration - 0.01f, unknown, unknown, $"#{pull} no terminal downtime");
			var stun = replay.PlayerDebuffs.Single(d => d.Name == "Stun" && d.Start >= p3.Spans[0][0] && d.End <= p3.Spans[^1][1]);
			var shell = replay.EnemyCasts!.Single(c => c.Key == p3.Key && c.Id == 40286);
			Expect(actual, stun.Start - 1, 1, shell.Start - stun.Start + 1, $"#{pull} UR forecast");
		}
		Console.WriteLine($"Downtime forecast: {checks} checks passed.");
	}
}
